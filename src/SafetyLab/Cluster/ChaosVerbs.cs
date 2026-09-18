using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>
/// The cluster-facing halves of <see cref="PostgresChaos"/>, <see cref="PodConfig"/> and the
/// marker counting the synth-guard summary needs. Decisions live in those pure classes; what is
/// here is the process invocation and the exit codes.
/// </summary>
public static class ChaosVerbs
{
    // ------------------------------------------------------------------ chaos

    public static int Arm()
    {
        var pg = PgPod();
        if (pg is null) return 2;

        // Refuse to arm on top of an existing arm. Two overlapping victims is not the experiment,
        // and the second disarm would report success having left the first one standing.
        var existing = ReadStatus(pg);
        if (existing == PostgresChaos.ArmState.Armed)
        {
            return Refuse("chaos is ALREADY ARMED on wolverine_nodes. A previous run did not disarm; " +
                          "run `safetylab chaos disarm` and check what it was measuring first.");
        }

        var nodes = Psql(pg, "select node_number || chr(9) || id || chr(9) || description " +
                            "from wolverine.wolverine_nodes order by node_number;");
        if (!nodes.Ok) return Refuse($"could not read the node registry: {nodes.Problem}");

        var leader = Psql(pg, "select node_id from wolverine.wolverine_node_assignments " +
                             "where id = 'wolverine://leader/';");
        if (!leader.Ok) return Refuse($"could not read the leader assignment: {leader.Problem}");

        if (!PostgresChaos.TryChooseVictim(
                PostgresChaos.ParseNodes(nodes.Value), leader.Value.Trim(), out var victim, out var problem))
        {
            return Refuse($"no usable victim: {problem}");
        }

        var armed = Psql(pg, PostgresChaos.ArmSql(victim!.Id));
        if (!armed.Ok) return Refuse($"the arm statement failed: {armed.Problem}");

        // Confirm from the server, not from the fact that the statement returned. An arm that did
        // not take is the same failure as no arm at all, and quieter.
        if (ReadStatus(pg) != PostgresChaos.ArmState.Armed)
        {
            return Refuse("the arm statement succeeded but the policy is not present. Nothing is injected.");
        }

        Console.WriteLine(victim.Id);
        Console.Error.WriteLine($"chaos: armed — node {victim.NodeNumber} ({victim.Description}) is now " +
                                "invisible to every read the app role makes");
        return 0;
    }

    /// <summary>
    /// Idempotent: disarming a disarmed cluster is a success, because the caller's trap runs on
    /// every exit path including the ones where arming never happened.
    /// </summary>
    public static int Disarm()
    {
        var pg = PgPod();
        if (pg is null) return 2;

        var result = Psql(pg, PostgresChaos.DisarmSql());
        if (!result.Ok) return Refuse($"the disarm statement failed: {result.Problem}");

        var state = ReadStatus(pg);
        if (state == PostgresChaos.ArmState.Armed)
        {
            return Refuse("the disarm statement succeeded but the policy is STILL PRESENT. " +
                          "The cluster is still injecting a fault — do not run another experiment on it.");
        }

        Console.Error.WriteLine("chaos: disarmed");
        return 0;
    }

    /// <summary>Prints armed/disarmed. Exit 1 when armed, so a caller can gate on it.</summary>
    public static int Status()
    {
        var pg = PgPod();
        if (pg is null) return 2;

        var state = ReadStatus(pg);
        Console.WriteLine(state.ToString().ToLowerInvariant());

        return state switch
        {
            PostgresChaos.ArmState.Armed => 1,
            PostgresChaos.ArmState.Disarmed => 0,
            _ => 2
        };
    }

    private static PostgresChaos.ArmState ReadStatus(string pod)
    {
        var result = Psql(pod, PostgresChaos.StatusSql());
        return result.Ok ? PostgresChaos.ParseStatus(result.Value) : PostgresChaos.ArmState.Unknown;
    }

    // --------------------------------------------------------- verify-config

    /// <summary>
    /// Assert every live pod reports the configuration this arm is supposed to have, from its own
    /// startup output. Exit 2 on any disagreement — this REFUSES where the shell version warned
    /// and carried on, which is how a run labelled "stock default" came to be measured with the
    /// settle gate on.
    ///
    /// Every live pod is checked, not the first. A rollout that half-applied an env var leaves the
    /// arms mixed, and the first pod alphabetically has no special authority.
    /// </summary>
    public static int VerifyConfig(string label, string[] expectations)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in expectations)
        {
            var split = raw.IndexOf('=');
            if (split <= 0)
            {
                Console.Error.WriteLine($"verify-config: '{raw}' is not Setting=Value");
                return 2;
            }

            expected[raw[..split]] = raw[(split + 1)..];
        }

        var pods = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!pods.Ok)
        {
            Console.Error.WriteLine($"verify-config: kubectl failed: {pods.StdErr.Trim()}");
            return 2;
        }

        var live = PodSelection.Live(pods.StdOut);
        if (live.Count == 0)
        {
            Console.Error.WriteLine($"verify-config: no live pod matching '{label}' to verify");
            return 2;
        }

        var failed = false;

        foreach (var pod in live)
        {
            var logs = ProcessRunner.Kubectl("logs", pod.Name);
            if (!logs.Ok)
            {
                Console.Error.WriteLine($"verify-config: could not read {pod.Name}: {logs.StdErr.Trim()}");
                failed = true;
                continue;
            }

            var reported = PodConfig.Parse(logs.StdOut.Split('\n'));

            if (reported.Count == 0)
            {
                // No CONFIG lines at all means the pod is not who we think it is, or is too young
                // to have written them. Either way nothing was verified, and "nothing verified"
                // must not read as "verified".
                Console.Error.WriteLine(
                    $"verify-config: {pod.Name} reported no CONFIG settings — it may be too " +
                    "young to have written them, or its startup output may have scrolled out of " +
                    "the retained container log");
                failed = true;
                continue;
            }

            var problems = PodConfig.Verify(reported, expected);
            if (problems.Count == 0)
            {
                Console.WriteLine($"  ok   {pod.Name}");
                continue;
            }

            failed = true;
            foreach (var problem in problems) Console.Error.WriteLine($"  FAIL {pod.Name}  {problem}");
        }

        if (!failed) return 0;

        Console.Error.WriteLine();
        Console.Error.WriteLine("verify-config: the cluster is not configured the way this run claims. " +
                                "Measuring it now files numbers under the wrong conditions.");
        return 2;
    }

    // ---------------------------------------------------------------- count

    /// <summary>
    /// How many log records carry a message containing <paramref name="contains"/>.
    ///
    /// This replaces <c>cat raw.*.jsonl | grep -c "…"</c>, and the difference is not cosmetic:
    /// grep counts LINES matching anywhere in the record, so a substring appearing inside a
    /// serialised exception, a stack frame or another field's value is counted as an occurrence of
    /// the event. This matches the Message field only. An empty directory is exit 2, where the
    /// shell pipeline printed 0.
    /// </summary>
    public static int Count(string directory, string contains)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"count: no such directory: {directory}");
            return 2;
        }

        var files = Directory.GetFiles(directory, "raw.*.jsonl");
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"count: no raw.*.jsonl in {directory}");
            return 2;
        }

        var total = 0;

        foreach (var path in files)
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('{')) continue;
                if (!trimmed.Contains(contains, StringComparison.Ordinal)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("Message", out var message) &&
                        message.GetString() is { } text &&
                        text.Contains(contains, StringComparison.Ordinal))
                    {
                        total++;
                    }
                }
                catch (JsonException)
                {
                    // Not a record.
                }
            }
        }

        Console.WriteLine(total);
        return 0;
    }

    // --------------------------------------------------------------- helpers

    private sealed record Query(bool Ok, string Value, string Problem);

    private static string? PgPod()
    {
        var pods = ProcessRunner.Kubectl("get", "pods", "-l", "app=pg", "-o", "json");
        if (!pods.Ok)
        {
            Refuse($"kubectl could not list the pg pods: {pods.StdErr.Trim()}");
            return null;
        }

        if (!PodSelection.TrySelectSingle(pods.StdOut, out var pod, out var problem) || pod is null)
        {
            Refuse($"no live PostgreSQL pod: {problem}");
            return null;
        }

        return pod.Name;
    }

    /// <summary>
    /// psql as the SUPERUSER, which is RLS-exempt — so this stays omniscient while the app role,
    /// which the fault targets, does not. That asymmetry is the experiment.
    /// </summary>
    private static Query Psql(string pod, string sql)
    {
        var result = ProcessRunner.Kubectl(
            "exec", pod, "--", "psql", "-U", "postgres", "-d", "churnsim", "-qAt", "-v", "ON_ERROR_STOP=1", "-c", sql);

        return result.Ok
            ? new Query(true, result.StdOut, "")
            : new Query(false, "", result.StdErr.Trim().Split('\n').FirstOrDefault() ?? $"exit {result.ExitCode}");
    }

    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"chaos: REFUSED — {problem}");
        return 2;
    }
}
