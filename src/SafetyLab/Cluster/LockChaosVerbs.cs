namespace SafetyLab.Cluster;

/// <summary>
/// The cluster-facing half of <see cref="LockChaos"/>: resolve the leader, resolve the backend
/// holding its advisory lock, signal it, and — the part that makes the run worth anything — read
/// the lock back afterwards and say whether it actually moved.
///
/// Not the arm/disarm shape of <see cref="ChaosVerbs"/> and <see cref="PartitionVerbs"/>, because
/// there is nothing to leak: killing a session is instantaneous and leaves no state behind for a
/// later run to trip over. What it shares with them is the refusal discipline — every way this
/// could signal the wrong process, or signal nothing and report a fault, is a named exit 2.
/// </summary>
public static class LockChaosVerbs
{
    /// <summary>
    /// Who holds the leadership lock right now, as <c>pid TAB client_addr TAB pod</c>.
    /// Exit 1 when nothing holds it, so a script can gate on "the cluster has a leader" before
    /// starting, and 2 when the store could not be read — which is a different fact entirely.
    /// </summary>
    public static int Status(string schema, long? lockIdOverride, string label)
    {
        var pg = PgPod();
        if (pg is null) return 2;

        var lockId = lockIdOverride ?? RunHistory.LockIdForSchema(schema);

        var holders = ReadHolders(pg, lockId);
        if (holders is null) return 2;

        var pods = LivePods(label);

        if (holders.Count == 0)
        {
            Console.WriteLine("none");
            Console.Error.WriteLine(
                $"lock-chaos: nothing holds advisory lock {lockId} (schema '{schema}'). Either the cluster has " +
                "no leader, or --schema/--lock-id do not match the one Wolverine took.");
            return 1;
        }

        foreach (var holder in holders)
        {
            var pod = pods.FirstOrDefault(x => x.Ip == holder.ClientAddr)?.Name ?? "unknown-pod";
            var state = holder.Granted ? "" : "\tWAITING";
            Console.WriteLine($"{holder.Pid}\t{holder.ClientAddr}\t{pod}{state}");
        }

        return holders.Any(x => x.Granted) ? 0 : 1;
    }

    /// <summary>
    /// Terminate (or cancel) the backend holding the leadership lock.
    ///
    /// Exit 0 only when the fault did what the mode claims; 2 on any refusal AND on a signal that
    /// landed without moving the lock. The second half is the point: "pg_terminate_backend
    /// returned true" is not evidence that a leader lost its lock, and a run that files it as such
    /// has measured an undisturbed cluster.
    /// </summary>
    public static int Kill(string mode, string schema, long? lockIdOverride, string label, bool dryRun)
    {
        var pg = PgPod();
        if (pg is null) return 2;

        var lockId = lockIdOverride ?? RunHistory.LockIdForSchema(schema);

        var leaderPod = Psql(pg, LockChaos.LeaderPodSql);
        if (!leaderPod.Ok) return Refuse($"could not read the leader assignment row: {leaderPod.Problem}");

        var leader = ResolveLeader(leaderPod.Value, label, out var why);
        if (leader is null && why.Length > 0) Console.Error.WriteLine($"lock-chaos: {why}");

        var holders = ReadHolders(pg, lockId);
        if (holders is null) return 2;

        if (!LockChaos.TryChooseTarget(holders, leader, lockId, out var target, out var problem))
        {
            return Refuse(problem);
        }

        Console.Error.WriteLine($"lock-chaos: advisory lock {lockId} (schema '{schema}') is held by " +
                                $"{target!.Describe()} = leader pod {leader!.Pod}");

        if (dryRun)
        {
            Console.WriteLine($"{target.Pid}\t{target.ClientAddr}\t{leader.Pod}");
            Console.Error.WriteLine($"lock-chaos: DRY RUN — would {mode} pid {target.Pid}. Nothing was signalled.");
            return 0;
        }

        var signal = Psql(pg, LockChaos.Sql(mode, target.Pid));
        if (!signal.Ok) return Refuse($"the {mode} statement failed: {signal.Problem}");

        if (!LockChaos.TryParseSignalled(signal.Value, out var signalled, out var unreadable))
        {
            return Refuse($"the {mode} statement returned something unreadable: {unreadable}");
        }

        // Read the lock back from the server. Doing this from the statement's return value alone
        // is how an injector ends up certifying its own success.
        var after = ReadHolders(pg, lockId);
        if (after is null) return 2;

        var outcome = LockChaos.Classify(target, after, out var detail);
        var verdict = LockChaos.Verdict(mode, signalled, outcome, detail);

        Console.WriteLine($"{target.Pid}\t{leader.Pod}\t{outcome.ToString().ToLowerInvariant()}");

        if (!verdict.Valid)
        {
            Console.Error.WriteLine($"lock-chaos: *** THE FAULT DID NOT FIRE *** — {verdict.Detail}");
            return 2;
        }

        Console.Error.WriteLine($"lock-chaos: {verdict.Detail}");
        return 0;
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// The pod the leader row names, resolved to a live pod with an IP. Every failure is
    /// reported rather than returned as an empty string, because <see cref="LockChaos.TryChooseTarget"/>
    /// refuses on a null leader and the operator needs to know which of the four reasons it was.
    /// </summary>
    private static LockChaos.LeaderPod? ResolveLeader(string leaderRow, string label, out string why)
    {
        why = "";

        var pod = leaderRow.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        if (pod is null)
        {
            why = "there is no leader assignment row at all — the cluster is mid-election or was never settled";
            return null;
        }

        var pods = LivePods(label);
        if (pods.Count == 0)
        {
            why = $"no live pod matches '{label}', so the leader row cannot be resolved to an address";
            return null;
        }

        var match = pods.FirstOrDefault(x => x.Name == pod);
        if (match is null)
        {
            // wolverine_nodes.description is Environment.MachineName, which is the pod name in
            // Kubernetes. A leader row naming a pod that is not live is a stale registration.
            why = $"the leader row names '{pod}', which is not among the live pods " +
                  $"({string.Join(", ", pods.Select(x => x.Name))}) — a stale node registration";
            return null;
        }

        if (match.Ip.Length == 0)
        {
            why = $"leader pod '{pod}' has no IP yet";
            return null;
        }

        return new LockChaos.LeaderPod(match.Name, match.Ip);
    }

    private static IReadOnlyList<PodInfo> LivePods(string label)
    {
        var pods = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        return pods.Ok ? PodSelection.Live(pods.StdOut) : [];
    }

    private static IReadOnlyList<LockChaos.Holder>? ReadHolders(string pod, long lockId)
    {
        var result = Psql(pod, LockChaos.HoldersSql(lockId));
        if (result.Ok) return LockChaos.ParseHolders(result.Value);

        Refuse($"could not read pg_locks: {result.Problem}");
        return null;
    }

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
    /// psql as the superuser, which is what <c>pg_terminate_backend</c> on another role's backend
    /// needs. <c>ON_ERROR_STOP=1</c> and a non-discarded stderr: a fault injector that cannot tell
    /// a failed statement from an empty result set is worse than none.
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
        Console.Error.WriteLine($"lock-chaos: REFUSED — {problem}");
        return 2;
    }
}
