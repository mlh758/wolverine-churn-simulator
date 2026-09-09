using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace SafetyLab;

/// <summary>
/// Polls the three pieces of server-side truth that Wolverine's leader election actually runs
/// on — the advisory locks in <c>pg_locks</c>, the node registry, and the agent assignment
/// table — and writes each sample to stdout as one JSON line.
///
/// Deliberately an outside observer on its own connection. Every existing check on this
/// machinery is an in-process assertion inside the node under test, which means it can only
/// ever confirm what that node *believes*. The bugs in this area are precisely the cases where
/// the belief and the server disagree: a leader whose backend was terminated but whose
/// in-process lock list still says "held" (GH-2602), or a lock stacked N deep by repeated
/// re-attainment and released once, still held server-side with nothing logged
/// (Bug_advisory_lock_stacking_blocks_failover). From out here both are plainly visible.
/// </summary>
public sealed class ClusterMonitor
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly long _leaderLockId;
    private readonly TimeSpan _tick;
    private readonly TextWriter _out;

    private NpgsqlConnection? _conn;
    private long _seq;

    /// <summary>Roughly once a minute at a 1s tick — cheap, and it makes any capture self-describing.</summary>
    private const int MetaEveryNSamples = 60;

    public ClusterMonitor(string connectionString, string schema, long leaderLockId, TimeSpan tick, TextWriter output)
    {
        _connectionString = connectionString;
        _schema = schema;
        _leaderLockId = leaderLockId;
        _tick = tick;
        _out = output;
    }

    public async Task RunAsync(CancellationToken token)
    {
        var serverVersion = await probeServerVersionAsync(token);
        var meta = new MetaRecord(DateTimeOffset.UtcNow, (int)_tick.TotalMilliseconds, _leaderLockId, _schema,
            serverVersion);
        writeLine(meta);

        // PeriodicTimer rather than a delay loop: a sample that takes 300ms must not push the
        // whole schedule out by 300ms. A tick missed because the previous one overran shows up
        // as a gap in `seq` vs wall-clock, which the coverage checker reports.
        using var timer = new PeriodicTimer(_tick);

        while (!token.IsCancellationRequested)
        {
            // Re-emit meta periodically. The monitor pod is long-lived and captures attach with
            // `kubectl logs --since`, so a capture that starts mid-life would otherwise never see
            // the one meta line written at startup -- and a history without meta loses the declared
            // tick and lock id, which is how the first live run reported 1,900 phantom sampling
            // gaps against an assumed default tick.
            if (_seq % MetaEveryNSamples == 0)
            {
                writeLine(meta);
            }

            await sampleOnceAsync(token);

            try
            {
                if (!await timer.WaitForNextTickAsync(token)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task sampleOnceAsync(CancellationToken token)
    {
        var seq = Interlocked.Increment(ref _seq);
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
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

            writeLine(new Sample(seq, started, dbNow, stopwatch.Elapsed.TotalMilliseconds, locks, nodes,
                assignments));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception e)
        {
            // Record the hole rather than dying. A monitor that exits on the first blip stops
            // watching exactly when the cluster gets interesting -- and a DB outage is itself a
            // scenario worth having in the history, not a reason to lose the run.
            await dropConnectionAsync();
            writeLine(new Sample(seq, started, null, stopwatch.Elapsed.TotalMilliseconds, [], [], [],
                $"{e.GetType().Name}: {e.Message}"));
        }
    }

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

    private void writeLine<T>(T record)
    {
        _out.WriteLine(JsonSerializer.Serialize(record, Json.Options));
        _out.Flush();
    }
}
