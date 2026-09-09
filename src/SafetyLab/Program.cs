using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SafetyLab;

return await Cli.RunAsync(args);

internal static partial class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var verb = args.FirstOrDefault();
        var rest = args.Skip(1).ToArray();

        switch (verb)
        {
            case "monitor": return await MonitorAsync(rest);
            case "harvest": return Harvest(rest);
            case "check": return Check(rest);
            default:
                Console.Error.WriteLine("""
                    safetylab — server-side invariant monitor for Wolverine leader election

                      safetylab monitor [--tick-ms N] [--schema S] [--lock-id N]
                          Poll pg_locks / wolverine_nodes / wolverine_node_assignments and write
                          one JSON sample per tick to stdout. Connection from POSTGRES_CONNECTION.

                      safetylab harvest --pod NAME
                          Read a pod's log text on stdin, write identity and AGENT-START/STOP
                          records to stdout as JSON. One process per pod.

                      safetylab check DIR [--grace S] [--cross-pod-grace S] [--converge S] [--json]
                          Run every checker over a captured run directory and report.
                          Exit 1 if any check failed.
                    """);
                return 2;
        }
    }

    // ---------------------------------------------------------------- monitor

    private static async Task<int> MonitorAsync(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                               ?? "Host=localhost;Port=5433;Database=churnsim;Username=postgres;Password=postgres";

        var tick = TimeSpan.FromMilliseconds(IntArg(args, "--tick-ms") ?? 200);
        var schema = StringArg(args, "--schema") ?? "wolverine";
        // Derived from the schema, because that is what Wolverine actually locks on
        // (PostgresqlNodePersistence._lockId = schemaName.GetDeterministicHashCode()). The
        // LeaderLockId = 9999999 constant in that same class is not used by the leadership path.
        var lockId = IntArg(args, "--lock-id") ?? RunHistory.LockIdForSchema(schema);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

        var monitor = new ClusterMonitor(connectionString, schema, lockId, tick, Console.Out);

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
    private static int Harvest(string[] args)
    {
        var pod = StringArg(args, "--pod");
        if (pod is null)
        {
            Console.Error.WriteLine("harvest: --pod NAME is required");
            return 2;
        }

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

    private static int Check(string[] args)
    {
        var directory = args.FirstOrDefault(x => !x.StartsWith('-'));
        if (directory is null)
        {
            Console.Error.WriteLine("check: a run directory is required");
            return 2;
        }

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

        var options = new CheckOptions
        {
            Grace = TimeSpan.FromSeconds(IntArg(args, "--grace") ?? 15),
            CrossPodGrace = TimeSpan.FromSeconds(IntArg(args, "--cross-pod-grace") ?? 2),
            ConvergenceWindow = TimeSpan.FromSeconds(IntArg(args, "--converge") ?? 60)
        };

        var results = Checkers.RunAll(history, options);

        if (args.Contains("--json"))
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
                              $"leader lock {meta.LeaderLockId}, schema '{meta.Schema}'");
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

    // ----------------------------------------------------------------- args

    private static string? StringArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static long? IntArg(string[] args, string name)
        => long.TryParse(StringArg(args, name), out var value) ? value : null;
}
