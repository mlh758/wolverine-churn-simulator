using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyLab;

/// <summary>
/// The verb implementations. Argument parsing lives in <see cref="CommandLineDefinition"/>; these
/// take values that are already typed and already validated, which is the point of the split.
/// </summary>
internal static partial class Verbs
{

    // ---------------------------------------------------------------- monitor

    public static async Task<int> MonitorAsync(string backend, TimeSpan tick, string schema, long? lockId,
        string service, int pageSize, string? url, string? database)
    {
        var cancellation = new CancellationTokenSource();

        void onProcessExit(object? sender, EventArgs e) => cancellation.Cancel();

        void onCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += onCancelKey;
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;

        IReadOnlyList<RavenClient> raven = [];

        try
        {
            MonitorLoop monitor;

            // Derived from the schema on both RDBMS arms, because that is what Wolverine actually
            // locks on -- PostgresqlNodePersistence._lockId and MySqlNodePersistence._lockId are
            // the same `schemaName.GetDeterministicHashCode()`, and MySQL merely spells the
            // resulting lock `wolverine_<id>` instead of passing the integer to
            // pg_try_advisory_lock. The LeaderLockId = 9999999 constant that sits in both classes
            // is not used by the leadership path on either.
            var leaderLockId = lockId ?? RunHistory.LockIdForSchema(schema);

            if (backend == Backends.RavenDb)
            {
                // One client per member. `--url` (or RAVENDB_URL) may be comma-separated; the
                // first is the primary view and the rest are read alongside it every tick. A
                // single url is the single-node arm, unchanged.
                raven = RavenQueries.ConnectAll(url, database);
                monitor = new RavenClusterMonitor(raven, service, pageSize, tick, Console.Out);
            }
            else if (backend == Backends.MySql)
            {
                var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION")
                                       ?? "Server=localhost;Port=3307;Database=churnsim;User ID=root;Password=churnsim";

                // The monitor refuses a schema that is not a plain identifier, because on MySQL it
                // is interpolated into the sampling query. That is a CONFIGURATION error and gets
                // exit 2 with its own message, the way every other "could not measure" outcome in
                // this tool does -- not a stack trace out of the top of the process.
                try
                {
                    monitor = new MySqlClusterMonitor(connectionString, schema, leaderLockId, tick, Console.Out);
                }
                catch (ArgumentException e)
                {
                    Console.Error.WriteLine($"monitor: {e.Message}");
                    return 2;
                }
            }
            else
            {
                var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                                       ?? "Host=localhost;Port=5433;Database=churnsim;Username=postgres;Password=postgres";
                monitor = new ClusterMonitor(connectionString, schema, leaderLockId, tick, Console.Out);
            }

            try
            {
                await monitor.RunAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }

            return 0;
        }
        finally
        {
            foreach (var client in raven) client.Dispose();

            Console.CancelKeyPress -= onCancelKey;
            AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
            cancellation.Dispose();
        }
    }

    // ---------------------------------------------------------------- harvest

    [GeneratedRegex(@"AGENT-(START|STOP)\s+(\S+)\s+at\s+(\S+)")]
    private static partial Regex AgentLine();

    [GeneratedRegex(@"SIM-IDENTITY\s+nodeId=(\S+)\s+podName=(\S+)\s+podIp=(\S+)\s+at\s+(\S+)")]
    private static partial Regex IdentityLine();

    /// <summary>The `kubectl logs --timestamps` RFC3339 prefix, if the follower asked for one.</summary>
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\S+?)\s+(.*)$")]
    private static partial Regex TimestampPrefix();

    /// <summary>
    /// The category line of .NET's two-line console logger, e.g.
    /// <c>info: Wolverine.Runtime.Agents.NodeAgentController[0]</c>. The message is on the NEXT
    /// line, indented — which is why this needs a state machine rather than a single match.
    /// </summary>
    [GeneratedRegex(@"^(info|warn|fail|dbug|trce|crit)\s*:\s*(Wolverine\.Runtime\.Agents\.[^\[]+|Wolverine\.Runtime\.WolverineRuntime)\[")]
    private static partial Regex ControlCategoryLine();

    /// <summary>
    /// Parse pod log text into history records. Timestamps come from the message body, not the log
    /// prefix, because the default console logger splits a record over two lines and the useful
    /// clock reading is the one the sim wrote itself.
    /// </summary>
    public static int Harvest(string pod)
    {
        // Set when the previous line was a Wolverine.Runtime.Agents category header and we are
        // waiting for its indented message line.
        (DateTimeOffset Ts, string Level, string Category)? pendingControl = null;

        while (Console.ReadLine() is { } raw)
        {
            // `kubectl logs --timestamps` prefixes every line. Strip it, but keep it: the console
            // logger's own lines carry no clock of their own, so this is the only timestamp a
            // control-plane line has. (AGENT-START/STOP embed theirs and keep using it.)
            var line = raw;
            DateTimeOffset? lineTs = null;
            var prefix = TimestampPrefix().Match(raw);
            if (prefix.Success)
            {
                lineTs = ParseTs(prefix.Groups[1].Value);
                line = prefix.Groups[2].Value;
            }

            if (pendingControl is { } pending)
            {
                pendingControl = null;
                var message = line.Trim();
                if (message.Length > 0)
                {
                    Emit(new ControlEventRecord(pending.Ts, pod, pending.Level, pending.Category, message));
                }
            }

            var control = ControlCategoryLine().Match(line);
            if (control.Success)
            {
                pendingControl = (lineTs ?? DateTimeOffset.UtcNow, control.Groups[1].Value,
                    control.Groups[2].Value.Trim());
                continue;
            }

            var identity = IdentityLine().Match(line);
            if (identity.Success && Guid.TryParse(identity.Groups[1].Value, out var nodeId))
            {
                Emit(new IdentityRecord(ParseTs(identity.Groups[4].Value), nodeId, identity.Groups[2].Value,
                    identity.Groups[3].Value));
                continue;
            }

            var agent = AgentLine().Match(line);
            if (agent.Success)
            {
                Emit(new AgentEventRecord(ParseTs(agent.Groups[3].Value),
                    agent.Groups[1].Value == "START" ? "start" : "stop", agent.Groups[2].Value, pod));
            }
        }

        return 0;

        static void Emit<T>(T record)
        {
            Console.WriteLine(JsonSerializer.Serialize(record, Json.Options));
            Console.Out.Flush();
        }
    }

    private static DateTimeOffset ParseTs(string raw)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : DateTimeOffset.UtcNow;

    // ------------------------------------------------------------------ check

    public static int Check(string directory, CheckOptions options, bool json)
    {
        RunHistory history;
        try
        {
            history = RunHistory.Load(directory);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"check: {e.Message}");
            return 2;
        }

        var results = Checkers.RunAll(history, options);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(results,
                new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        }
        else
        {
            Report(history, results);
        }

        return results.Any(x => x.Failed) ? 1 : 0;
    }

    private static void Report(RunHistory history, IReadOnlyList<CheckResult> results)
    {
        Console.WriteLine($"SafetyLab report — {history.Directory}");
        if (history.Meta is { } meta)
        {
            Console.WriteLine($"  started {meta.StartedUtc:u}, {meta.TickMs}ms tick, " +
                              $"backend '{history.Backend}', {history.LeaderLockDescription}, " +
                              $"{(history.IsRavenDb ? "database" : "schema")} '{meta.Schema}'");
        }

        Console.WriteLine();

        foreach (var result in results)
        {
            var status = result.Skipped ? "SKIP" : result.Failed ? "FAIL" : "PASS";
            Console.WriteLine($"[{status}] {result.Id}  {result.Title}");

            foreach (var note in result.Notes)
            {
                Console.WriteLine($"         · {note}");
            }

            foreach (var violation in result.Violations.Take(10))
            {
                Console.WriteLine($"         ! {violation.From:HH:mm:ss}–{violation.To:HH:mm:ss} " +
                                  $"({violation.Duration.TotalSeconds:F1}s) {violation.Detail}");
            }

            if (result.Violations.Count > 10)
            {
                Console.WriteLine($"         ! … and {result.Violations.Count - 10} more");
            }

            Console.WriteLine();
        }

        var failed = results.Count(x => x.Failed);
        var skipped = results.Count(x => x.Skipped);
        Console.WriteLine(failed == 0
            ? $"{results.Count - skipped} check(s) passed, {skipped} skipped."
            : $"{failed} check(s) FAILED, {skipped} skipped.");

        if (skipped > 0)
        {
            Console.WriteLine("A skipped check is not a passing one — see its note for the missing input.");
        }
    }

}
