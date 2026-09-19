namespace SafetyLab.Cluster;

/// <summary>
/// The network-partition fault, as data: which pods are cut from which, what firewall rules that
/// is, and how to read those rules back off the node. Pure, so every rule below is testable
/// against captured text with no cluster.
///
/// <b>Where the cut is made.</b> minikube's default CNI (kindnet, <c>ptp</c>) gives every pod its
/// own veth pair and a /32 host route, so pod-to-pod traffic on the one node is <em>forwarded</em>
/// by the host kernel and traverses the <c>FORWARD</c> chain. A <c>DROP</c> there, keyed on source
/// and destination pod IP, cuts exactly one ordered pair and nothing else — no pod is touched,
/// nothing runs inside a namespace, and the rules are visible to <c>iptables-nft -S FORWARD</c> from
/// the node. The node's legacy <c>iptables</c> binary cannot load its tables under rootless podman
/// (<c>ip_tables</c> is not present); <c>iptables-nft</c> is what kube-proxy's own rules are written
/// with, and it works.
///
/// <b>Why it is symmetric.</b> Dropping only A→B leaves B→A packets arriving and A's replies
/// dropped, which for TCP is a slow stall rather than a cut, and for RavenDB's replication looks
/// like a lagging peer rather than an absent one. Both directions are cut for every cross pair.
///
/// <b>Why the observer is on neither side.</b> The monitor's pod is never in either set, so it
/// keeps reading every store member through the partition. That is the whole point of reading
/// per member: the finding is the difference between the sides, and an observer inside one side
/// could only report that side.
///
/// <b>Every rule carries a comment</b>, and the comment is the only thing <see cref="Heal"/> and
/// the status read look for. A rule without it is not this rig's, and is left alone.
/// </summary>
public static class Partition
{
    public const string Comment = "safetylab-partition";

    /// <summary>What one side of the cut is made of: named pods with the IPs the rules key on.</summary>
    public sealed record Endpoint(string Pod, string Ip);

    public sealed record Plan(IReadOnlyList<Endpoint> Isolated, IReadOnlyList<Endpoint> Others)
    {
        /// <summary>Every ordered cross pair, both directions — the rule specs to insert.</summary>
        public IReadOnlyList<string> RuleSpecs()
        {
            var specs = new List<string>();

            foreach (var a in Isolated)
            {
                foreach (var b in Others)
                {
                    specs.Add(Spec(a.Ip, b.Ip));
                    specs.Add(Spec(b.Ip, a.Ip));
                }
            }

            return specs;
        }

        public string Describe()
            => $"isolate [{string.Join(", ", Isolated.Select(x => $"{x.Pod}={x.Ip}"))}] from " +
               $"[{string.Join(", ", Others.Select(x => $"{x.Pod}={x.Ip}"))}]";
    }

    /// <summary>One rule, in the form <c>iptables-nft -S</c> prints it back (minus the chain).</summary>
    public static string Spec(string from, string to)
        => $"-s {from}/32 -d {to}/32 -m comment --comment {Comment} -j DROP";

    /// <summary>
    /// Resolve pod names to endpoints, refusing anything that cannot be cut cleanly. Every named
    /// pod must exist, be live, and have an IP; a pod in both sets is a contradiction; a side with
    /// no members is a partition of nothing. Each refusal is named, because a plan that silently
    /// dropped a pod would arm a partial partition and report it as a whole one.
    /// </summary>
    public static bool TryPlan(IReadOnlyList<PodInfo> pods, IReadOnlyList<string> isolate,
        IReadOnlyList<string> others, out Plan? plan, out string problem)
    {
        plan = null;
        problem = "";

        if (isolate.Count == 0 || others.Count == 0)
        {
            problem = "both sides of the cut need at least one pod";
            return false;
        }

        var overlap = isolate.Intersect(others, StringComparer.Ordinal).ToArray();
        if (overlap.Length > 0)
        {
            problem = $"pod(s) named on both sides of the cut: {string.Join(", ", overlap)}";
            return false;
        }

        var byName = pods.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);

        var isolated = Resolve(byName, isolate, out problem);
        if (isolated is null) return false;

        var rest = Resolve(byName, others, out problem);
        if (rest is null) return false;

        plan = new Plan(isolated, rest);
        return true;
    }

    private static IReadOnlyList<Endpoint>? Resolve(IReadOnlyDictionary<string, PodInfo> byName,
        IReadOnlyList<string> names, out string problem)
    {
        problem = "";
        var list = new List<Endpoint>();

        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var pod))
            {
                problem = $"no pod named '{name}'";
                return null;
            }

            if (!pod.IsLive)
            {
                problem = $"pod '{name}' is not live (phase={pod.Phase}, ready={pod.Ready}, " +
                          $"terminating={pod.Terminating}) — a rule on a pod about to be replaced cuts nothing";
                return null;
            }

            if (pod.Ip.Length == 0)
            {
                problem = $"pod '{name}' has no IP yet";
                return null;
            }

            list.Add(new Endpoint(name, pod.Ip));
        }

        return list;
    }

    /// <summary>
    /// One shell command that inserts every rule at the top of FORWARD. Inserted, not appended:
    /// kube-proxy's <c>KUBE-FORWARD</c> chain accepts established flows early in the chain, and a
    /// DROP appended after it would cut new connections while every existing one carried on.
    /// </summary>
    public static string ArmCommand(Plan plan)
        => string.Join(" && ", plan.RuleSpecs().Select(spec => $"sudo iptables-nft -I FORWARD 1 {spec}"));

    public const string ListCommand = "sudo iptables-nft -S FORWARD";

    /// <summary>
    /// The rules of ours currently on the node, exactly as <c>-S FORWARD</c> printed them. Only
    /// lines carrying the comment count; kube-proxy's rules in the same chain are not ours to
    /// touch, and a heal that deleted them would take the cluster's own networking with it.
    /// </summary>
    public static IReadOnlyList<string> ArmedRules(string listOutput)
        => listOutput.Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("-A FORWARD ", StringComparison.Ordinal) &&
                        x.Contains($"--comment {Comment}", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The delete for every armed rule: the same text with <c>-A</c> turned into <c>-D</c>, which
    /// is why the rules are read back rather than reconstructed — the kernel's own spelling of a
    /// rule is the only one guaranteed to match it for deletion.
    /// </summary>
    public static string HealCommand(IReadOnlyList<string> armedRules)
        => string.Join(" && ", armedRules.Select(rule => "sudo iptables-nft -D FORWARD " + rule["-A FORWARD ".Length..]));

    /// <summary>
    /// Did every planned rule land? Compared as sets of specs, because insertion order at the top
    /// of the chain reverses them and <c>-S</c> prints them in chain order.
    /// </summary>
    public static bool AllPresent(Plan plan, IReadOnlyList<string> armedRules, out IReadOnlyList<string> missing)
    {
        var present = armedRules.Select(x => x["-A FORWARD ".Length..]).ToHashSet(StringComparer.Ordinal);
        missing = plan.RuleSpecs().Where(spec => !present.Contains(spec)).ToList();
        return missing.Count == 0;
    }
}
