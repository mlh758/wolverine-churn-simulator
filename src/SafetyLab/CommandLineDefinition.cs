using System.CommandLine;
using SafetyLab.Cluster;

namespace SafetyLab;

/// <summary>
/// The whole CLI surface, in one place, as data.
///
/// This replaced a hand-rolled parser built on <c>Array.IndexOf(args, "--name")</c> plus a
/// hand-maintained usage heredoc. The reason to change it is not tidiness — it is that the old
/// parser could not fail. A misspelled flag simply did not match, <c>StringArg</c> returned null,
/// and the verb ran against its default: <c>--databse churnsim2</c> silently measured
/// <c>churnsim</c>, and <c>--tick-ms banana</c> silently sampled at 200ms. For a rig whose entire
/// discipline is "no claim without the configuration it was measured under", a configuration
/// surface that discards what it does not understand is the wrong shape. Unknown tokens are now a
/// parse error with a non-zero exit, and the help text is generated from these definitions rather
/// than maintained alongside them.
///
/// The surface itself is UNCHANGED. Every verb and flag is spelled exactly as the scripts and the
/// k8s manifests already invoke them — <c>monitor --backend ravendb --tick-ms 1000 --service
/// churnsim</c> and the rest — because breaking those would be a far bigger cost than the parser
/// ever was. <see cref="Build"/> is public so the parse rules can be unit-tested without running
/// anything (tests/SafetyLab.Tests).
/// </summary>
public static class CommandLineDefinition
{
    // Shared across the RavenDB-facing verbs. Declared once so query, admin and monitor cannot
    // drift into disagreeing about what "--database" means.
    private static Option<string?> UrlOption() => new("--url")
    {
        Description = "RavenDB base url (default: RAVENDB_URL, else http://ravendb:8080)"
    };

    private static Option<string?> DatabaseOption() => new("--database")
    {
        Description = "RavenDB database (default: RAVENDB_DATABASE, else churnsim)"
    };

    public static RootCommand Build()
    {
        var root = new RootCommand(
            "safetylab — server-side invariant monitor for Wolverine leader election");

        root.Subcommands.Add(BuildMonitor());
        root.Subcommands.Add(BuildHarvest());
        root.Subcommands.Add(BuildCheck());
        root.Subcommands.Add(BuildQuery());
        root.Subcommands.Add(BuildAdmin());
        root.Subcommands.Add(BuildPods());
        root.Subcommands.Add(BuildPickPod());
        root.Subcommands.Add(BuildHostPid());

        return root;
    }

    // ---------------------------------------------------------------- monitor

    private static Command BuildMonitor()
    {
        var backend = new Option<string>("--backend")
        {
            Description = "message store to watch",
            DefaultValueFactory = _ => Backends.Postgres
        };
        // The old code checked this by hand and fell through to an error message. Declaring it
        // means an unknown backend is rejected by the parser, with the valid values listed.
        backend.AcceptOnlyFromAmong(Backends.Postgres, Backends.RavenDb);

        var tick = new Option<int>("--tick-ms")
        {
            Description = "sampling interval in milliseconds",
            DefaultValueFactory = _ => 200
        };

        var schema = new Option<string>("--schema")
        {
            Description = "PostgreSQL schema", DefaultValueFactory = _ => "wolverine"
        };

        var lockId = new Option<long?>("--lock-id")
        {
            Description = "override the advisory lock id (default: derived from --schema)"
        };

        var service = new Option<string>("--service")
        {
            Description = "Wolverine ServiceName, which forms the RavenDB leadership key",
            DefaultValueFactory = _ => "churnsim"
        };

        var pageSize = new Option<int>("--page-size")
        {
            Description = "max rows per RavenDB read; exceeding it is reported, never silent",
            DefaultValueFactory = _ => 2000
        };

        var url = UrlOption();
        var database = DatabaseOption();

        var command = new Command("monitor",
            "Poll the store's leadership lock, node registry and agent assignments; one JSON sample per tick to stdout.");

        foreach (var option in new Option[] { backend, tick, schema, lockId, service, pageSize, url, database })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, _) => Verbs.MonitorAsync(
            parseResult.GetValue(backend)!,
            TimeSpan.FromMilliseconds(parseResult.GetValue(tick)),
            parseResult.GetValue(schema)!,
            parseResult.GetValue(lockId),
            parseResult.GetValue(service)!,
            parseResult.GetValue(pageSize),
            parseResult.GetValue(url),
            parseResult.GetValue(database)));

        return command;
    }

    // ---------------------------------------------------------------- harvest

    private static Command BuildHarvest()
    {
        var pod = new Option<string>("--pod") { Description = "pod name to attribute records to", Required = true };

        var command = new Command("harvest",
            "Read a pod's log text on stdin; write identity, AGENT-START/STOP and control-plane records as JSON.");
        command.Options.Add(pod);
        command.SetAction(parseResult => Verbs.Harvest(parseResult.GetValue(pod)!));

        return command;
    }

    // ------------------------------------------------------------------ check

    private static Command BuildCheck()
    {
        var directory = new Argument<string>("directory") { Description = "a captured run directory" };

        var grace = new Option<int>("--grace")
        {
            Description = "seconds a divergence must persist to count", DefaultValueFactory = _ => 15
        };
        var crossPod = new Option<int>("--cross-pod-grace")
        {
            Description = "slack for comparing two pods' clocks", DefaultValueFactory = _ => 2
        };
        var converge = new Option<int>("--converge")
        {
            Description = "seconds a converged state must hold", DefaultValueFactory = _ => 60
        };
        var json = new Option<bool>("--json") { Description = "emit results as JSON" };

        var command = new Command("check",
            "Run every checker over a captured run directory. Exit 1 if any check failed.");
        command.Arguments.Add(directory);
        foreach (var option in new Option[] { grace, crossPod, converge, json }) command.Options.Add(option);

        command.SetAction(parseResult => Verbs.Check(
            parseResult.GetValue(directory)!,
            new CheckOptions
            {
                Grace = TimeSpan.FromSeconds(parseResult.GetValue(grace)),
                CrossPodGrace = TimeSpan.FromSeconds(parseResult.GetValue(crossPod)),
                ConvergenceWindow = TimeSpan.FromSeconds(parseResult.GetValue(converge))
            },
            parseResult.GetValue(json)));

        return command;
    }

    // ------------------------------------------------------------------ query

    /// <summary>
    /// Each measurement is its own subcommand rather than a free-text KIND argument, so
    /// <c>query assigend</c> is rejected with the valid list instead of falling through to an
    /// error string the caller has to notice. The spelling on the command line is identical.
    /// </summary>
    private static Command BuildQuery()
    {
        var command = new Command("query",
            "RavenDB only: read one measurement as TSV on stdout, the way the PostgreSQL scripts use psql.");

        var url = UrlOption();
        var database = DatabaseOption();
        url.Recursive = true;
        database.Recursive = true;
        command.Options.Add(url);
        command.Options.Add(database);

        (string Name, string Description)[] kinds =
        [
            ("assigned", "agentUri <TAB> pod name, sim:// agents only"),
            ("placed", "count of placed sim:// agents"),
            ("leader", "pod <TAB> node id owning wolverine://leader/, or 'none'"),
            ("lock", "the leadership compare-exchange value and its expiry"),
            ("nodes", "node number <TAB> description (= pod name)"),
            ("per-node", "node id <TAB> agent count"),
            ("records", "record type <TAB> count, descending")
        ];

        foreach (var (name, description) in kinds)
        {
            var sub = new Command(name, description);
            sub.SetAction((parseResult, _) => RavenQueries.RunAsync(
                name, parseResult.GetValue(url), parseResult.GetValue(database), "AssignmentChanged"));
            command.Subcommands.Add(sub);
        }

        // The only kind that takes a parameter of its own.
        var eventOption = new Option<string>("--event")
        {
            Description = "node record type to bucket", DefaultValueFactory = _ => "AssignmentChanged"
        };
        var perMinute = new Command("per-minute", "minute <TAB> count for one record type");
        perMinute.Options.Add(eventOption);
        perMinute.SetAction((parseResult, _) => RavenQueries.RunAsync(
            "per-minute", parseResult.GetValue(url), parseResult.GetValue(database),
            parseResult.GetValue(eventOption)!));
        command.Subcommands.Add(perMinute);

        return command;
    }

    // ------------------------------------------------------------------ admin

    private static Command BuildAdmin()
    {
        var command = new Command("admin", "RavenDB only, and it MUTATES.");

        var url = UrlOption();
        var database = DatabaseOption();
        url.Recursive = true;
        database.Recursive = true;
        command.Options.Add(url);
        command.Options.Add(database);

        var reset = new Command("reset-metrics", "delete every NodeRecords document, and wait for it");
        reset.SetAction((parseResult, _) => RavenQueries.AdminAsync(
            "reset-metrics", parseResult.GetValue(url), parseResult.GetValue(database)));

        var drop = new Command("drop-database", "hard-delete the database outright");
        drop.SetAction((parseResult, _) => RavenQueries.AdminAsync(
            "drop-database", parseResult.GetValue(url), parseResult.GetValue(database)));

        command.Subcommands.Add(reset);
        command.Subcommands.Add(drop);

        return command;
    }

    // ------------------------------------------------------- host-side targeting

    private static Command BuildPods()
    {
        var label = new Option<string>("--label")
        {
            Description = "label selector", DefaultValueFactory = _ => "app=churnsim"
        };
        var all = new Option<bool>("--all") { Description = "skip the liveness filter" };

        var command = new Command("pods",
            "Live pods matching a label (Running AND Ready AND not terminating), one per line.");
        command.Options.Add(label);
        command.Options.Add(all);
        command.SetAction(parseResult =>
            HostTools.Pods(parseResult.GetValue(label)!, !parseResult.GetValue(all)));

        return command;
    }

    private static Command BuildPickPod()
    {
        var label = new Option<string>("--label")
        {
            Description = "label selector", DefaultValueFactory = _ => "app=churnsim"
        };

        var command = new Command("pick-pod",
            "Exactly one live pod, or exit 2 with the reason. What '-o jsonpath={.items[0]...}' pretended to be.");
        command.Options.Add(label);
        command.SetAction(parseResult => HostTools.PickPod(parseResult.GetValue(label)!));

        return command;
    }

    private static Command BuildHostPid()
    {
        var pod = new Option<string>("--pod") { Description = "pod whose container to resolve", Required = true };
        var container = new Option<string>("--container")
        {
            Description = "container name", DefaultValueFactory = _ => "churnsim"
        };
        var expect = new Option<string>("--expect")
        {
            Description = "substring that must appear in /proc/<pid>/cmdline",
            DefaultValueFactory = _ => "ChurnSim"
        };

        var command = new Command("host-pid",
            "Host pid of a pod's container, from crictl's .info.pid, refused unless >1 and the cmdline matches.");
        foreach (var option in new Option[] { pod, container, expect }) command.Options.Add(option);

        command.SetAction(parseResult => HostTools.HostPid(
            parseResult.GetValue(pod)!, parseResult.GetValue(container)!, parseResult.GetValue(expect)!));

        return command;
    }
}
