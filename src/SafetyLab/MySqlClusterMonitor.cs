using MySqlConnector;

namespace SafetyLab;

/// <summary>
/// The MySQL arm's monitor — the same outside observer as <see cref="ClusterMonitor"/>, watching
/// the same three pieces of server-side truth (the leadership lock, the node registry, the agent
/// assignment table) and writing the same <see cref="Sample"/> shape, so every checker runs over a
/// MySQL capture without knowing it is one.
///
/// That is possible because MySQL's leadership lock is structurally PostgreSQL's:
/// <c>MySqlAdvisoryLock</c> takes <c>GET_LOCK('wolverine_&lt;id&gt;', 0)</c> on one long-lived
/// connection, where the id is the same <c>schemaName.GetDeterministicHashCode()</c>, and a named
/// lock dies with its session exactly as an advisory lock dies with its backend. Belief-versus-
/// server is therefore the same question here (GH-2602), and so is the stacking failure that
/// <c>Bug_advisory_lock_stacking_blocks_failover</c> pins: a lock re-attained once per heartbeat
/// and released once stays held server-side with nothing logged.
///
/// Reading it back is where MySQL differs, and the difference is worth the two queries it costs:
///
/// <list type="number">
///   <item>
///     <b><c>IS_USED_LOCK(name)</c> is the authority</b> and needs nothing switched on. It returns
///     the connection id holding that exact name, or NULL. The leader lock's row is built from it.
///   </item>
///   <item>
///     <b><c>performance_schema.metadata_locks</c> is how you enumerate the OTHERS</b> —
///     <c>OBJECT_TYPE = 'USER LEVEL LOCK'</c> — and it is only populated while the
///     <c>wait/lock/metadata/sql/mdl</c> instrument is on. It is on by default in MySQL 8 and
///     k8s/mysql.yaml declares it anyway, but a monitor that assumed it and got an empty table
///     would report "no locks held" on every tick and every leader check would pass over nothing.
///     So the two are cross-checked: a tick where <c>IS_USED_LOCK</c> names a holder that
///     <c>metadata_locks</c> does not know about carries a WARNING, and the coverage check prints
///     it. A hole in the evidence has to be visible.
///   </item>
/// </list>
/// </summary>
public sealed class MySqlClusterMonitor : MonitorLoop
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly long _leaderLockId;
    private readonly TimeSpan _tick;

    private MySqlConnection? _conn;

    public MySqlClusterMonitor(string connectionString, string schema, long leaderLockId, TimeSpan tick,
        TextWriter output)
        : base(tick, output)
    {
        AssertSchemaIsAnIdentifier(schema);

        _connectionString = connectionString;
        _schema = schema;
        _leaderLockId = leaderLockId;
        _tick = tick;
    }

    /// <summary>
    /// A schema name is an IDENTIFIER, so it cannot be a parameter and has to be interpolated into
    /// the sampling query. <c>--schema</c> is free text that <c>AcceptOnlyFromAmong</c> cannot
    /// constrain, so it is checked here instead of trusted: refuse anything that is not a plain
    /// MySQL identifier, rather than let a backtick or a semicolon out of the CLI and into the
    /// query text. Throwing at construction means the monitor never starts, which is the right
    /// outcome — a monitor that starts and samples nothing is the failure this rig fears most.
    /// </summary>
    public static void AssertSchemaIsAnIdentifier(string schema)
    {
        if (schema.Length is > 0 and <= 64 && schema.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$'))
        {
            return;
        }

        throw new ArgumentException(
            $"--schema '{schema}' is not a plain MySQL identifier (letters, digits, '_' or '$'; " +
            "1-64 characters). On MySQL the schema is a database name and it is interpolated into " +
            "the sampling query, so it is refused rather than quoted around.", nameof(schema));
    }

    /// <summary>
    /// How <c>MySqlAdvisoryLock.ToLockName</c> spells a lock id. Reimplemented rather than
    /// referenced, for the same reason as <see cref="RunHistory.LockIdForSchema"/>: the monitor
    /// keeps no Wolverine dependency, so it can watch a cluster running any build.
    /// </summary>
    public static string LockName(long lockId) => $"wolverine_{(int)lockId}";

    /// <summary>
    /// The integer a Wolverine lock name carries, or null for a name of any other shape. Used to
    /// fill <see cref="LockRow.ObjId"/> so that <c>RunHistory.LeaderLockHolders</c> — which
    /// compares the low 32 bits of ObjId against the leader lock id — works on this arm unchanged.
    /// </summary>
    public static long? LockIdFromName(string? name)
        => name is not null && name.StartsWith("wolverine_", StringComparison.Ordinal) &&
           int.TryParse(name["wolverine_".Length..], out var id)
            ? id
            : null;

    protected override async Task<MetaRecord> DescribeAsync(CancellationToken token)
    {
        return new MetaRecord(DateTimeOffset.UtcNow, (int)_tick.TotalMilliseconds, _leaderLockId, _schema,
            await probeServerVersionAsync(token), Backends.MySql, LockName(_leaderLockId));
    }

    protected override async Task<Sample> TakeSampleAsync(long seq, DateTimeOffset started, CancellationToken token)
    {
        var conn = await connectionAsync(token);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 5;
        cmd.Parameters.AddWithValue("@leaderLock", LockName(_leaderLockId));

        // Five result sets in one command, the same shape the PostgreSQL monitor uses, so a tick
        // is one round trip and the five readings are as close to simultaneous as the server will
        // make them.
        //
        // PROCESSLIST_HOST rather than information_schema.processlist.HOST: the latter appends
        // ":port" to the address, and the identity map joins on a bare pod IP.
        //
        // No `like 'wolverine\_%'` escape games -- 'wolverine%' is unambiguous here and cannot be
        // read wrong. LOCK_STATUS is carried through to Granted rather than filtered on, because a
        // PENDING row is a session WAITING for that lock, and dropping it would hide a queue.
        //
        // The schema is an IDENTIFIER, so it cannot be a parameter and is interpolated -- which is
        // safe only because AssertSchemaIsAnIdentifier refused anything but a plain one before
        // this object was constructed.
        cmd.CommandText = $"""
            select t.processlist_id, ml.object_name, ml.lock_status,
                   t.processlist_host, t.processlist_command, t.processlist_state, t.processlist_time
              from performance_schema.metadata_locks ml
              join performance_schema.threads t on t.thread_id = ml.owner_thread_id
             where ml.object_type = 'USER LEVEL LOCK'
               and ml.object_name like 'wolverine%';

            select t.processlist_id, t.processlist_host, t.processlist_command,
                   t.processlist_state, t.processlist_time
              from performance_schema.threads t
             where t.processlist_id = is_used_lock(@leaderLock);

            select id, node_number, health_check, description from `{_schema}`.wolverine_nodes;

            select id, node_id, started from `{_schema}`.wolverine_node_assignments;

            select utc_timestamp(6);
            """;

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var locks = new List<LockRow>();
        var leaderSeenInMetadata = false;
        while (await reader.ReadAsync(token))
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            var id = LockIdFromName(name);
            var granted = !reader.IsDBNull(2) &&
                          string.Equals(reader.GetString(2), "GRANTED", StringComparison.OrdinalIgnoreCase);

            // Only a GRANTED row counts as "metadata_locks can see the holder". A PENDING one is
            // a session queued behind the holder, and reading it as coverage would silence the
            // warning below on exactly the server where it matters.
            if (granted && id == (int)_leaderLockId) leaderSeenInMetadata = true;

            locks.Add(new LockRow(
                Pid: ToPid(reader.GetValue(0)),
                ClassId: 0,
                ObjId: id ?? 0,
                ObjSubId: 0,
                Granted: granted,
                ApplicationName: name,
                ClientAddr: reader.IsDBNull(3) ? null : reader.GetString(3),
                BackendState: Describe(reader, 4, 5),
                BackendStart: NotOlderThan(reader, 6, started)));
        }

        // The authority. Replaces whatever metadata_locks said about the leader lock rather than
        // sitting beside it: two GRANTED rows for one lock would read as two holders, which is
        // S1's failure, and a sentinel that can fire on its own reporting is worth nothing. Only
        // the granted row goes -- a PENDING waiter on the same lock is a different fact and stays.
        var warnings = new List<string>();
        await reader.NextResultAsync(token);
        if (await reader.ReadAsync(token))
        {
            locks.RemoveAll(x => (uint)x.ObjId == (uint)_leaderLockId && x.Granted);
            locks.Add(new LockRow(
                Pid: ToPid(reader.GetValue(0)),
                ClassId: 0,
                ObjId: (int)_leaderLockId,
                ObjSubId: 0,
                Granted: true,
                ApplicationName: LockName(_leaderLockId),
                ClientAddr: reader.IsDBNull(1) ? null : reader.GetString(1),
                BackendState: Describe(reader, 2, 3),
                BackendStart: NotOlderThan(reader, 4, started)));

            if (!leaderSeenInMetadata)
            {
                warnings.Add(
                    $"is_used_lock('{LockName(_leaderLockId)}') names a holder that " +
                    "performance_schema.metadata_locks does not list. The leader lock below is still " +
                    "server-side truth, but every OTHER Wolverine lock is invisible in this sample — " +
                    "the wait/lock/metadata/sql/mdl instrument is off on this server.");
            }
        }

        var nodes = new List<NodeRow>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            nodes.Add(new NodeRow(
                reader.GetGuid(0),
                reader.GetInt32(1),
                Utc(reader.GetDateTime(2)),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }

        var assignments = new List<AssignmentRow>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            assignments.Add(new AssignmentRow(
                reader.GetString(0),
                reader.GetGuid(1),
                Utc(reader.GetDateTime(2))));
        }

        DateTimeOffset? dbNow = null;
        await reader.NextResultAsync(token);
        if (await reader.ReadAsync(token))
        {
            dbNow = Utc(reader.GetDateTime(0));
        }

        return new Sample(seq, started, dbNow, 0, locks, nodes, assignments,
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    /// <summary>
    /// Weasel maps <c>DateTimeOffset</c> to a bare <c>DATETIME</c> on MySQL, so every timestamp
    /// comes back naive. Wolverine writes UTC into these columns and their server-side defaults
    /// are <c>UTC_TIMESTAMP(6)</c>, so UTC is what they are — but the driver cannot know that, and
    /// letting .NET stamp them Local would put the whole history hours away from the monitor's own
    /// clock and hand the clock-offset note a fiction.
    ///
    /// Note the column type is <c>DATETIME</c> and not <c>DATETIME(6)</c>: these carry WHOLE
    /// SECONDS on this arm where PostgreSQL's timestamptz carries microseconds.
    /// </summary>
    private static DateTimeOffset Utc(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>
    /// MySQL connection ids are <c>BIGINT UNSIGNED</c> and <see cref="LockRow.Pid"/> is an int,
    /// which is what every consumer prints and what <c>KILL</c> takes. A server that has been up
    /// long enough to wrap past int.MaxValue would truncate; clamp instead of wrapping, so an
    /// impossible id is obviously impossible rather than plausibly wrong.
    /// </summary>
    private static int ToPid(object raw)
    {
        var value = Convert.ToInt64(raw);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : int.MaxValue;
    }

    /// <summary>"Sleep / (idle)" — the pair of columns that together answer pg_stat_activity.state.</summary>
    private static string? Describe(MySqlDataReader reader, int command, int state)
    {
        var parts = new[] { command, state }
            .Select(i => reader.IsDBNull(i) ? null : reader.GetString(i))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>
    /// <c>PROCESSLIST_TIME</c> counts seconds in the CURRENT state, not since the connection was
    /// opened, so this is a lower bound on the connection's age and nothing stronger. Recorded
    /// because "the holder's connection is seconds old" and "it is hours old" are different
    /// stories about a failover, and neither is knowable otherwise.
    /// </summary>
    private static DateTimeOffset? NotOlderThan(MySqlDataReader reader, int index, DateTimeOffset now)
        => reader.IsDBNull(index) ? null : now.AddSeconds(-Convert.ToInt64(reader.GetValue(index)));

    protected override Task OnSampleFailedAsync() => dropConnectionAsync();

    private async Task<MySqlConnection> connectionAsync(CancellationToken token)
    {
        if (_conn is { State: System.Data.ConnectionState.Open })
        {
            return _conn;
        }

        await dropConnectionAsync();

        var conn = new MySqlConnection(_connectionString);
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
            cmd.CommandText = "select concat(version(), ' / mdl instrument ', " +
                              "coalesce((select enabled from performance_schema.setup_instruments " +
                              "           where name = 'wait/lock/metadata/sql/mdl'), 'ABSENT'))";
            return (await cmd.ExecuteScalarAsync(token))?.ToString() ?? "unknown";
        }
        catch (Exception e)
        {
            return $"unavailable at startup: {e.Message}";
        }
    }
}
