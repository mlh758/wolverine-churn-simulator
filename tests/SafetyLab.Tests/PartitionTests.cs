using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The network partition's plan, rules and read-back. Same rule as the RLS injector: a fault
/// injector must prove it fired and prove it stopped, and every way this could cut less than it
/// claims — or delete a rule that is not ours — is pinned here without a node.
/// </summary>
public class PartitionTests
{
    private static PodInfo Live(string name, string ip) => new(name, "Running", true, false, 0, ip);

    private static readonly IReadOnlyList<PodInfo> Pods =
    [
        Live("ravendb-0", "10.244.0.10"),
        Live("ravendb-1", "10.244.0.11"),
        Live("ravendb-2", "10.244.0.12"),
        Live("churnsim-0", "10.244.0.20"),
        Live("churnsim-1", "10.244.0.21"),
        Live("churnsim-2", "10.244.0.22"),
        Live("safetylab-abc", "10.244.0.30")
    ];

    // ------------------------------------------------------------------- plan

    [Fact]
    public void every_cross_pair_is_cut_in_both_directions_and_nothing_else()
    {
        Assert.True(Partition.TryPlan(Pods, ["ravendb-2", "churnsim-2"],
            ["ravendb-0", "ravendb-1", "churnsim-0", "churnsim-1"], out var plan, out _));

        var specs = plan!.RuleSpecs();

        // 2 isolated × 4 others × 2 directions.
        Assert.Equal(16, specs.Count);
        Assert.Contains(Partition.Spec("10.244.0.12", "10.244.0.10"), specs);
        Assert.Contains(Partition.Spec("10.244.0.10", "10.244.0.12"), specs);
        Assert.Contains(Partition.Spec("10.244.0.22", "10.244.0.11"), specs);

        // The isolated pods still reach each other, and the observer is untouched.
        Assert.DoesNotContain(specs, s => s.Contains("10.244.0.12") && s.Contains("10.244.0.22"));
        Assert.DoesNotContain(specs, s => s.Contains("10.244.0.30"));
    }

    [Fact]
    public void an_unknown_pod_is_refused_rather_than_dropped_from_the_plan()
    {
        // A plan that silently lost a pod would arm a partial partition and report a whole one.
        Assert.False(Partition.TryPlan(Pods, ["ravendb-9"], ["ravendb-0"], out var plan, out var problem));
        Assert.Null(plan);
        Assert.Contains("ravendb-9", problem);
    }

    [Fact]
    public void a_pod_that_is_not_live_is_refused()
    {
        var pods = Pods.Append(new PodInfo("churnsim-3", "Running", true, true, 0, "10.244.0.23")).ToList();

        Assert.False(Partition.TryPlan(pods, ["churnsim-3"], ["ravendb-0"], out _, out var problem));
        Assert.Contains("not live", problem);
        Assert.Contains("terminating=True", problem);
    }

    [Fact]
    public void a_pod_without_an_ip_is_refused()
    {
        var pods = Pods.Append(Live("churnsim-3", "")).ToList();

        Assert.False(Partition.TryPlan(pods, ["churnsim-3"], ["ravendb-0"], out _, out var problem));
        Assert.Contains("no IP", problem);
    }

    [Fact]
    public void a_pod_on_both_sides_is_a_contradiction()
    {
        Assert.False(Partition.TryPlan(Pods, ["ravendb-2"], ["ravendb-2", "ravendb-0"], out _, out var problem));
        Assert.Contains("both sides", problem);
    }

    [Fact]
    public void an_empty_side_is_refused()
    {
        Assert.False(Partition.TryPlan(Pods, [], ["ravendb-0"], out _, out var problem));
        Assert.Contains("at least one pod", problem);
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void rules_are_inserted_at_the_top_of_forward_and_carry_the_comment()
    {
        Assert.True(Partition.TryPlan(Pods, ["ravendb-2"], ["ravendb-0"], out var plan, out _));

        var command = Partition.ArmCommand(plan!);

        // At position 1, ahead of kube-proxy's KUBE-FORWARD accept for established flows;
        // appended after it, existing connections would carry on through the "partition".
        Assert.Contains("iptables-nft -I FORWARD 1 -s 10.244.0.12/32 -d 10.244.0.10/32", command);
        Assert.Contains("iptables-nft -I FORWARD 1 -s 10.244.0.10/32 -d 10.244.0.12/32", command);
        Assert.Equal(2, command.Split(" && ").Length);
        Assert.All(command.Split(" && "), part => Assert.Contains($"--comment {Partition.Comment} -j DROP", part));
    }

    private const string ForwardChain = """
        -P FORWARD ACCEPT
        -A FORWARD -s 10.244.0.10/32 -d 10.244.0.12/32 -m comment --comment safetylab-partition -j DROP
        -A FORWARD -s 10.244.0.12/32 -d 10.244.0.10/32 -m comment --comment safetylab-partition -j DROP
        -A FORWARD -m conntrack --ctstate NEW -m comment --comment "kubernetes load balancer firewall" -j KUBE-PROXY-FIREWALL
        -A FORWARD -m comment --comment "kubernetes forwarding rules" -j KUBE-FORWARD
        """;

    [Fact]
    public void only_our_rules_are_read_back_from_the_chain()
    {
        var armed = Partition.ArmedRules(ForwardChain);

        // kube-proxy's rules share the chain and are not ours to touch: a heal that deleted them
        // would take the cluster's own networking down with the partition.
        Assert.Equal(2, armed.Count);
        Assert.All(armed, rule => Assert.Contains(Partition.Comment, rule));
        Assert.DoesNotContain(armed, rule => rule.Contains("KUBE"));
    }

    [Fact]
    public void the_heal_deletes_exactly_what_was_read_back()
    {
        var armed = Partition.ArmedRules(ForwardChain);
        var command = Partition.HealCommand(armed);

        Assert.Equal(
            "sudo iptables-nft -D FORWARD -s 10.244.0.10/32 -d 10.244.0.12/32 -m comment --comment safetylab-partition -j DROP" +
            " && " +
            "sudo iptables-nft -D FORWARD -s 10.244.0.12/32 -d 10.244.0.10/32 -m comment --comment safetylab-partition -j DROP",
            command);
    }

    [Fact]
    public void an_intact_chain_reads_as_nothing_armed()
    {
        var intact = """
            -P FORWARD ACCEPT
            -A FORWARD -m comment --comment "kubernetes forwarding rules" -j KUBE-FORWARD
            """;

        Assert.Empty(Partition.ArmedRules(intact));
        Assert.Equal("", Partition.HealCommand([]));
    }

    [Fact]
    public void a_partial_arm_is_detected_by_the_read_back()
    {
        Assert.True(Partition.TryPlan(Pods, ["ravendb-2"], ["ravendb-0", "ravendb-1"], out var plan, out _));

        // Only the ravendb-0 pair landed; the ravendb-1 pair did not.
        Assert.False(Partition.AllPresent(plan!, Partition.ArmedRules(ForwardChain), out var missing));
        Assert.Equal(2, missing.Count);
        Assert.All(missing, spec => Assert.Contains("10.244.0.11", spec));
    }

    [Fact]
    public void a_complete_arm_is_confirmed_regardless_of_chain_order()
    {
        Assert.True(Partition.TryPlan(Pods, ["ravendb-2"], ["ravendb-0"], out var plan, out _));

        // -I FORWARD 1 reverses insertion order; -S prints chain order. Compared as sets.
        Assert.True(Partition.AllPresent(plan!, Partition.ArmedRules(ForwardChain), out var missing));
        Assert.Empty(missing);
    }
}
