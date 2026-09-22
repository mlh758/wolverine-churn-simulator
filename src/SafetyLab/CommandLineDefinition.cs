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
/// than maintained alongside them. Both are properties of the parser rather than of this file, so
/// neither is restated as a test.
///
/// The surface itself is UNCHANGED. Every verb and flag is spelled exactly as the scripts and the
/// k8s manifests already invoke them — <c>monitor --backend ravendb --tick-ms 1000 --service
/// churnsim</c> and the rest — because breaking those would be a far bigger cost than the parser
/// ever was.
/// </summary>
public static class CommandLineDefinition
{
    // Shared across the RavenDB-facing verbs. Declared once so query, admin and monitor cannot
    // drift into disagreeing about what "--database" means.
    private static Option<string?> UrlOption() => new("--url")
    {
        Description = "RavenDB base url (default: RAVENDB_URL, else http://ravendb:8080). The monitor and " +
                      "raven-cluster accept a comma-separated list, one per cluster member, primary first."
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
        root.Subcommands.Add(BuildSnapshot());
        root.Subcommands.Add(BuildOverlaps());
        root.Subcommands.Add(BuildTraces());
        root.Subcommands.Add(BuildChaos());
        root.Subcommands.Add(BuildLockChaos());
        root.Subcommands.Add(BuildVerifyConfig());
        root.Subcommands.Add(BuildCount());
        root.Subcommands.Add(BuildSettle());
        root.Subcommands.Add(BuildPartition());
        root.Subcommands.Add(BuildRavenCluster());

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
        backend.AcceptOnlyFromAmong(Backends.All);

        var tick = new Option<int>("--tick-ms")
        {
            Description = "sampling interval in milliseconds",
            DefaultValueFactory = _ => 200
        };

        var schema = new Option<string>("--schema")
        {
            // On MySQL the schema IS a database; the flag stays spelled the same because it names
            // the same thing to Wolverine (DatabaseSettings.SchemaName) and derives the same lock
            // id on both RDBMS arms.
            Description = "PostgreSQL schema / MySQL database holding the Wolverine tables",
            DefaultValueFactory = _ => "wolverine"
        };

        var lockId = new Option<long?>("--lock-id")
        {
            Description = "override the leadership lock id (default: derived from --schema)"
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

        // Replication conflicts, current and resolved. `--since` bounds the resolved-revision read;
        // the partition script passes the partition-start time.
        var since = new Option<string>("--since")
        {
            Description = "ISO-8601 instant; resolved conflicts older than this are not listed",
            DefaultValueFactory = _ => DateTimeOffset.UtcNow.AddHours(-24).ToString("O")
        };
        var conflicts = new Command("conflicts",
            "replicated RavenDB: current document conflicts and resolved-conflict revisions. Exit 1 if any.");
        conflicts.Options.Add(since);
        conflicts.SetAction((parseResult, _) => RavenQueries.RunAsync(
            "conflicts", parseResult.GetValue(url), parseResult.GetValue(database), parseResult.GetValue(since)!));
        command.Subcommands.Add(conflicts);

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

    // --------------------------------------------------------------- snapshot

    /// <summary>
    /// The three-way exit code is the whole point and is stated in the help text, because a caller
    /// that treats 2 as 1 — or as 0 — has reintroduced the defect this verb was written to remove.
    /// </summary>
    private static Command BuildSnapshot()
    {
        var directory = new Argument<string>("directory")
        {
            Description = "where to write raw.*.jsonl, running.tsv, assigned.tsv, report.txt and snapshot.json"
        };

        var label = new Option<string>("--label")
        {
            Description = "label selector for the app pods", DefaultValueFactory = _ => "app=churnsim"
        };

        var tsv = new Option<bool>("--tsv")
        {
            Description = "print 'verdict running assigned duplicated orphaned missing' instead of the summary line"
        };

        var command = new Command("snapshot",
            "Compare agents actually running (from pod logs) against the assignment table. " +
            "Exit 0 clean, 1 diverged, 2 COULD NOT MEASURE.");

        command.Arguments.Add(directory);
        command.Options.Add(label);
        command.Options.Add(tsv);

        command.SetAction(parseResult => Snapshot.Run(
            parseResult.GetValue(directory)!,
            parseResult.GetValue(label)!,
            parseResult.GetValue(tsv)));

        return command;
    }

    // --------------------------------------------------------------- overlaps

    private static Command BuildOverlaps()
    {
        var directory = new Argument<string>("directory")
        {
            Description = "a directory of captured raw.*.jsonl pod logs"
        };

        var grace = new Option<double>("--grace")
        {
            Description = "seconds an overlap must exceed to count; below this it is handover, not duplication",
            DefaultValueFactory = _ => 2.0
        };

        var tsv = new Option<bool>("--tsv")
        {
            Description = "print 'outcome healed persisted longest_heal_s' instead of the summary line"
        };

        var command = new Command("overlaps",
            "Replay pod logs into agent residencies and classify every cross-pod overlap. " +
            "Exit 0 never, 1 overlap found, 2 COULD NOT ANALYSE.");

        command.Arguments.Add(directory);
        command.Options.Add(grace);
        command.Options.Add(tsv);

        command.SetAction(parseResult => Overlaps.Run(
            parseResult.GetValue(directory)!,
            TimeSpan.FromSeconds(parseResult.GetValue(grace)),
            parseResult.GetValue(tsv)));

        return command;
    }

    // ----------------------------------------------------------------- traces

    private static Command BuildTraces()
    {
        var minutes = new Option<int>("--minutes")
        {
            Description = "lookback window", DefaultValueFactory = _ => 30
        };

        var service = new Option<string>("--service")
        {
            Description = "Jaeger service name", DefaultValueFactory = _ => "churnsim"
        };

        var operation = new Option<string>("--operation")
        {
            Description = "span operation to summarise",
            DefaultValueFactory = _ => "wolverine_node_assignments"
        };

        var raw = new Option<bool>("--raw") { Description = "print the Jaeger payload and nothing else" };

        var command = new Command("traces",
            "Summarise Wolverine's spans from Jaeger. Exit 2 if they could not be read — including " +
            "when there are none, which is a configuration answer rather than a quiet system.");

        foreach (var option in new Option[] { minutes, service, operation, raw }) command.Options.Add(option);

        command.SetAction(parseResult => Traces.Run(
            parseResult.GetValue(minutes),
            parseResult.GetValue(service)!,
            parseResult.GetValue(operation)!,
            parseResult.GetValue(raw)));

        return command;
    }

    // ------------------------------------------------------------------ chaos

    /// <summary>
    /// The fault injector, PostgreSQL only. Split into arm / disarm / status precisely so a caller
    /// can guarantee the disarm from a trap and check for a leaked arm before starting.
    /// </summary>
    private static Command BuildChaos()
    {
        var command = new Command("chaos",
            "PostgreSQL only, and it MUTATES: hide one node's row from the app role's reads.");

        var arm = new Command("arm",
            "Choose a non-leader victim and hide its node row. Prints the victim id. Exit 2 if no " +
            "victim is safe to choose, or if chaos is already armed.");
        arm.SetAction(_ => ChaosVerbs.Arm());

        var disarm = new Command("disarm",
            "Remove the fault. Idempotent, so a trap can call it on every exit path.");
        disarm.SetAction(_ => ChaosVerbs.Disarm());

        var status = new Command("status",
            "Print armed/disarmed. Exit 1 when armed, so a run can refuse to start on a dirty cluster.");
        status.SetAction(_ => ChaosVerbs.Status());

        command.Subcommands.Add(arm);
        command.Subcommands.Add(disarm);
        command.Subcommands.Add(status);

        return command;
    }

    // ----------------------------------------------------------- lock-chaos

    /// <summary>
    /// The lock-session fault (E8), PostgreSQL only. No disarm, because there is nothing to leak:
    /// a terminated session leaves no state behind. What it has instead is <c>--dry-run</c> and a
    /// post-read, for the same reason <c>leader-kill.sh</c> has both — a fault injector ought to
    /// be able to show what it would destroy, and it must prove afterwards that it fired.
    /// </summary>
    private static Command BuildLockChaos()
    {
        var command = new Command("lock-chaos",
            "PostgreSQL only, and it MUTATES: take the leadership advisory lock away from the leader " +
            "by killing the backend holding it.");

        var schema = new Option<string>("--schema")
        {
            Description = "PostgreSQL schema, which is what the lock id is derived from",
            DefaultValueFactory = _ => "wolverine"
        };
        var lockId = new Option<long?>("--lock-id")
        {
            Description = "override the advisory lock id (default: derived from --schema, exactly as the monitor does)"
        };
        var label = new Option<string>("--label")
        {
            Description = "label selector for the app pods, used to resolve the leader row to an IP",
            DefaultValueFactory = _ => "app=churnsim"
        };

        schema.Recursive = true;
        lockId.Recursive = true;
        label.Recursive = true;
        command.Options.Add(schema);
        command.Options.Add(lockId);
        command.Options.Add(label);

        var status = new Command("status",
            "Print 'pid <TAB> client_addr <TAB> pod' for every backend on the leadership lock. " +
            "Exit 1 when nothing holds it, 2 when the store could not be read.");
        status.SetAction(parseResult => LockChaosVerbs.Status(
            parseResult.GetValue(schema)!, parseResult.GetValue(lockId), parseResult.GetValue(label)!));

        var mode = new Option<string>("--mode")
        {
            Description = "terminate kills the session and the lock dies with it; cancel interrupts the " +
                          "statement and leaves both alive — the control arm",
            DefaultValueFactory = _ => LockChaos.Terminate
        };
        mode.AcceptOnlyFromAmong(LockChaos.Terminate, LockChaos.Cancel);

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "resolve the leader and the backend holding its lock, print them, and stop before signalling"
        };

        var kill = new Command("kill",
            "Signal the backend holding the leadership lock, then read the lock back. " +
            "Exit 0 only if the fault did what the mode claims; 2 on a refusal, and on a signal that " +
            "landed without moving the lock.");
        kill.Options.Add(mode);
        kill.Options.Add(dryRun);
        kill.SetAction(parseResult => LockChaosVerbs.Kill(
            parseResult.GetValue(mode)!,
            parseResult.GetValue(schema)!,
            parseResult.GetValue(lockId),
            parseResult.GetValue(label)!,
            parseResult.GetValue(dryRun)));

        command.Subcommands.Add(status);
        command.Subcommands.Add(kill);

        return command;
    }

    // -------------------------------------------------------------- partition

    /// <summary>
    /// The network partition, same three-verb shape as <c>chaos</c> and for the same reason: the
    /// heal goes in a trap, and a run refuses to start over a cut that a previous run left behind.
    /// </summary>
    private static Command BuildPartition()
    {
        var command = new Command("partition",
            "MUTATES the minikube node's FORWARD chain: cut pod-to-pod traffic between two sets of pods, both ways.");

        var isolate = new Option<string[]>("--isolate")
        {
            Description = "pods on the minority side, comma-separated or repeated",
            Required = true,
            AllowMultipleArgumentsPerToken = false
        };
        var from = new Option<string[]>("--from")
        {
            Description = "pods on the majority side, comma-separated or repeated",
            Required = true,
            AllowMultipleArgumentsPerToken = false
        };

        var arm = new Command("arm",
            "Insert DROP rules for every cross pair and confirm from the node that all of them landed. " +
            "Exit 2 if any pod is not live, or a partition is already armed.");
        arm.Options.Add(isolate);
        arm.Options.Add(from);
        arm.SetAction(parseResult => PartitionVerbs.Arm(
            Split(parseResult.GetValue(isolate)), Split(parseResult.GetValue(from))));

        var heal = new Command("heal",
            "Remove every rule this tool armed and confirm none remain. Idempotent, so a trap can call it.");
        heal.SetAction(_ => PartitionVerbs.Heal());

        var status = new Command("status",
            "Print intact/armed with the rules. Exit 1 when armed, so a run can refuse to start.");
        status.SetAction(_ => PartitionVerbs.Status());

        command.Subcommands.Add(arm);
        command.Subcommands.Add(heal);
        command.Subcommands.Add(status);

        return command;

        static string[] Split(string[]? values)
            => (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
    }

    // ---------------------------------------------------------- raven-cluster

    private static Command BuildRavenCluster()
    {
        var command = new Command("raven-cluster",
            "Replicated RavenDB only: form the cluster and create the database at a replication factor, or describe it.");

        var url = UrlOption();
        var database = DatabaseOption();
        url.Recursive = true;
        database.Recursive = true;
        command.Options.Add(url);
        command.Options.Add(database);

        var factor = new Option<int>("--replication-factor")
        {
            Description = "members the database is created across", DefaultValueFactory = _ => 3
        };

        var form = new Command("form",
            "Bootstrap the first url, add the rest as members, create the database. Idempotent. MUTATES the store. " +
            "Must run before the first ChurnSim pod, which would otherwise create the database at factor 1.");
        form.Options.Add(factor);
        form.SetAction((parseResult, _) => RavenClusterAdmin.FormAsync(
            parseResult.GetValue(url), parseResult.GetValue(database), parseResult.GetValue(factor)));

        var status = new Command("status", "Each member's view of the cluster, and the database's group topology.");
        status.SetAction((parseResult, _) => RavenClusterAdmin.StatusAsync(
            parseResult.GetValue(url), parseResult.GetValue(database)));

        var preferred = new Command("preferred",
            "The member a default (un-pinned) client prefers: the first node of the database group's topology.");
        preferred.SetAction((parseResult, _) => RavenClusterAdmin.PreferredAsync(
            parseResult.GetValue(url), parseResult.GetValue(database)));

        command.Subcommands.Add(form);
        command.Subcommands.Add(status);
        command.Subcommands.Add(preferred);

        return command;
    }

    // --------------------------------------------------------- verify-config

    private static Command BuildVerifyConfig()
    {
        var label = new Option<string>("--label")
        {
            Description = "label selector for the pods to verify", DefaultValueFactory = _ => "app=churnsim"
        };

        var expect = new Option<string[]>("--expect")
        {
            Description = "Setting=Value, as the pod's own CONFIG line reports it; repeatable",
            AllowMultipleArgumentsPerToken = false,
            Required = true
        };

        var command = new Command("verify-config",
            "Assert every live pod reports this configuration from its own startup output. " +
            "Exit 2 on any disagreement — it refuses rather than warns.");

        command.Options.Add(label);
        command.Options.Add(expect);

        command.SetAction(parseResult => ChaosVerbs.VerifyConfig(
            parseResult.GetValue(label)!, parseResult.GetValue(expect) ?? []));

        return command;
    }

    // ----------------------------------------------------------------- settle

    private static Command BuildSettle()
    {
        var expect = new Option<int?>("--expect")
        {
            Description = "agents to wait for (default: SIM_AGENT_COUNT, read from the deployment)"
        };
        var timeout = new Option<int>("--timeout-seconds")
        {
            Description = "give up after this long", DefaultValueFactory = _ => 1200
        };
        var poll = new Option<int>("--poll-seconds")
        {
            Description = "seconds between reads", DefaultValueFactory = _ => 10
        };
        var stable = new Option<int>("--stable-polls")
        {
            Description = "consecutive polls the count must hold", DefaultValueFactory = _ => 3
        };
        var series = new Option<string?>("--series")
        {
            Description = "write the placement series here, one 'elapsed<TAB>placed' per poll"
        };

        var command = new Command("settle",
            "Wait for full placement to hold still. Prints elapsed seconds. " +
            "Exit 0 settled, 1 did not settle, 2 COULD NOT MEASURE.");

        foreach (var option in new Option[] { expect, timeout, poll, stable, series }) command.Options.Add(option);

        command.SetAction(parseResult => Settle.Run(
            parseResult.GetValue(expect),
            parseResult.GetValue(timeout),
            parseResult.GetValue(poll),
            parseResult.GetValue(stable),
            parseResult.GetValue(series)));

        return command;
    }

    // ------------------------------------------------------------------ count

    private static Command BuildCount()
    {
        var directory = new Argument<string>("directory")
        {
            Description = "a directory of captured raw.*.jsonl pod logs"
        };

        var contains = new Option<string>("--message")
        {
            Description = "count records whose Message contains this text", Required = true
        };

        var command = new Command("count",
            "Count log records by message text — the Message field only, not any line that happens " +
            "to contain the string. Exit 2 if there are no logs to count.");

        command.Arguments.Add(directory);
        command.Options.Add(contains);

        command.SetAction(parseResult => ChaosVerbs.Count(
            parseResult.GetValue(directory)!, parseResult.GetValue(contains)!));

        return command;
    }
}
