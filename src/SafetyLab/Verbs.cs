using System.Text.Json;
using System.Text.RegularExpressions;

using SafetyLab.Cluster;

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

    /// <summary>
    /// Pod log text on stdin to history records on stdout. The parsing is <see cref="LogHarvest"/>,
    /// which reads both console shapes into the same records; this is only the plumbing.
    /// </summary>
    public static int Harvest(string pod)
    {
        foreach (var record in LogHarvest.Parse(pod, ReadLines()))
        {
            Console.WriteLine(JsonSerializer.Serialize(record, record.GetType(), Json.Options));
            Console.Out.Flush();
        }

        return 0;

        static IEnumerable<string> ReadLines()
        {
            while (Console.ReadLine() is { } line) yield return line;
        }
    }

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
