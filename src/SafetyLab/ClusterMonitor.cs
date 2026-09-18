using System.Net;
using Npgsql;

namespace SafetyLab;

/// <summary>
/// Polls the three pieces of server-side truth that Wolverine's PostgreSQL leader election
/// actually runs on — the advisory locks in <c>pg_locks</c>, the node registry, and the agent
/// assignment table — and writes each sample to stdout as one JSON line.
///
/// Deliberately an outside observer on its own connection. Every existing check on this
/// machinery is an in-process assertion inside the node under test, which means it can only
/// ever confirm what that node *believes*. The bugs in this area are precisely the cases where
/// the belief and the server disagree: a leader whose backend was terminated but whose
/// in-process lock list still says "held" (GH-2602), or a lock stacked N deep by repeated
/// re-attainment and released once, still held server-side with nothing logged
/// (Bug_advisory_lock_stacking_blocks_failover). From out here both are plainly visible.
///
/// See <see cref="RavenClusterMonitor"/> for the RavenDB arm, which watches a structurally
/// different lock: a compare-exchange document with an expiry rather than a session-scoped lock.
/// </summary>
public sealed class ClusterMonitor : MonitorLoop
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly long _leaderLockId;
    private readonly TimeSpan _tick;

    private NpgsqlConnection? _conn;

    public ClusterMonitor(string connectionString, string schema, long leaderLockId, TimeSpan tick, TextWriter output)
        : base(tick, output)
    {
        _connectionString = connectionString;
        _schema = schema;
        _leaderLockId = leaderLockId;
        _tick = tick;
    }

    protected override async Task<MetaRecord> DescribeAsync(CancellationToken token)
    {
        return new MetaRecord(DateTimeOffset.UtcNow, (int)_tick.TotalMilliseconds, _leaderLockId, _schema,
            await probeServerVersionAsync(token), Backends.Postgres);
    }

    protected override async Task<Sample> TakeSampleAsync(long seq, DateTimeOffset started, CancellationToken token)
    {
        var conn = await connectionAsync(token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 5;
        cmd.CommandText = $"""
            select l.pid, l.classid, l.objid, l.objsubid, l.granted,
                   a.application_name, a.client_addr, a.state, a.backend_start
              from pg_locks l
              left join pg_stat_activity a on a.pid = l.pid
             where l.locktype = 'advisory';

            select id, node_number, health_check, description from {_schema}.wolverine_nodes;

            select id, node_id, started from {_schema}.wolverine_node_assignments;

            select now();
            """;

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var locks = new List<LockRow>();
        while (await reader.ReadAsync(token))
        {
            locks.Add(new LockRow(
                reader.GetInt32(0),
                Convert.ToInt64(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)),
                reader.GetInt16(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : ((IPAddress)reader.GetValue(6)).ToString(),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        var nodes = new List<NodeRow>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            nodes.Add(new NodeRow(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }

        var assignments = new List<AssignmentRow>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            assignments.Add(new AssignmentRow(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        }

        DateTimeOffset? dbNow = null;
        await reader.NextResultAsync(token);
        if (await reader.ReadAsync(token))
        {
            dbNow = reader.GetFieldValue<DateTimeOffset>(0);
        }

        return new Sample(seq, started, dbNow, 0, locks, nodes, assignments);
    }

    protected override Task OnSampleFailedAsync() => dropConnectionAsync();

    private async Task<NpgsqlConnection> connectionAsync(CancellationToken token)
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
        {
            return _conn;
        }

        await dropConnectionAsync();

        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(token);
        _conn = conn;
        return conn;
    }

    private async Task dropConnectionAsync()
    {
        if (_conn is null) return;

        try
        {
            await _conn.DisposeAsync();
        }
        catch
        {
            // Already broken; nothing to do.
        }

        _conn = null;
    }

    private async Task<string> probeServerVersionAsync(CancellationToken token)
    {
        try
        {
            var conn = await connectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "select version()";
            return (await cmd.ExecuteScalarAsync(token))?.ToString() ?? "unknown";
        }
        catch (Exception e)
        {
            return $"unavailable at startup: {e.Message}";
        }
    }
}
