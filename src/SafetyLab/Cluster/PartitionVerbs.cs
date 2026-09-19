namespace SafetyLab.Cluster;

/// <summary>
/// The cluster-facing half of <see cref="Partition"/>: resolve pods, run the rules on the node,
/// and — the part that matters — read them back to prove the arm took and the heal finished.
/// Same three verbs as the PostgreSQL chaos injector, for the same reason: a caller can put the
/// heal in a trap and refuse to start on a cluster where a previous cut is still standing.
/// </summary>
public static class PartitionVerbs
{
    public static int Arm(string[] isolate, string[] others)
    {
        var existing = readArmed();
        if (existing is null) return 2;

        if (existing.Count > 0)
        {
            return Refuse($"a partition is ALREADY ARMED ({existing.Count} rule(s) on the node). A previous run " +
                          "did not heal; run `safetylab partition heal` and check what it was measuring first.");
        }

        var pods = ProcessRunner.Kubectl("get", "pods", "-o", "json");
        if (!pods.Ok) return Refuse($"kubectl could not list pods: {pods.StdErr.Trim()}");

        if (!Partition.TryPlan(PodSelection.Parse(pods.StdOut), isolate, others, out var plan, out var problem))
        {
            return Refuse(problem);
        }

        var armed = ProcessRunner.MinikubeSsh(Partition.ArmCommand(plan!));
        if (!armed.Ok)
        {
            // Some rules may have landed before the failing one. Say so and leave them for the
            // caller's heal, rather than pretending the node is clean.
            return Refuse($"inserting the rules failed on the node: {armed.StdErr.Trim()} — run `partition heal` " +
                          "before anything else, some rules may be in place");
        }

        // Confirm from the node, not from the fact that the command returned.
        var after = readArmed();
        if (after is null) return 2;

        if (!Partition.AllPresent(plan!, after, out var missing))
        {
            return Refuse($"{missing.Count} of {plan!.RuleSpecs().Count} rules are not on the node after arming; " +
                          "the partition is PARTIAL. Heal and retry.");
        }

        Console.WriteLine(plan!.Describe());
        Console.Error.WriteLine($"partition: armed — {after.Count} FORWARD rule(s) cut both directions of every cross pair");
        return 0;
    }

    /// <summary>Idempotent: healing an intact cluster is a success, because traps run on every exit path.</summary>
    public static int Heal()
    {
        var armed = readArmed();
        if (armed is null) return 2;

        if (armed.Count == 0)
        {
            Console.Error.WriteLine("partition: nothing armed");
            return 0;
        }

        var healed = ProcessRunner.MinikubeSsh(Partition.HealCommand(armed));
        if (!healed.Ok) return Refuse($"deleting the rules failed on the node: {healed.StdErr.Trim()}");

        var after = readArmed();
        if (after is null) return 2;

        if (after.Count > 0)
        {
            return Refuse($"{after.Count} rule(s) are STILL on the node after the heal. The cluster is still cut — " +
                          "do not run another experiment on it.");
        }

        Console.Error.WriteLine($"partition: healed — {armed.Count} rule(s) removed");
        return 0;
    }

    /// <summary>Prints the armed rules. Exit 1 when any are present, so a run can gate on it.</summary>
    public static int Status()
    {
        var armed = readArmed();
        if (armed is null) return 2;

        if (armed.Count == 0)
        {
            Console.WriteLine("intact");
            return 0;
        }

        Console.WriteLine($"armed ({armed.Count} rules)");
        foreach (var rule in armed) Console.WriteLine("  " + rule);
        return 1;
    }

    private static IReadOnlyList<string>? readArmed()
    {
        var listed = ProcessRunner.MinikubeSsh(Partition.ListCommand);
        if (listed.Ok) return Partition.ArmedRules(listed.StdOut);

        Refuse($"could not read the FORWARD chain from the node: {listed.StdErr.Trim()}");
        return null;
    }

    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"partition: REFUSED — {problem}");
        return 2;
    }
}
