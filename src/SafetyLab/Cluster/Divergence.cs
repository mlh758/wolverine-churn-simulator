using System.Text.Json.Serialization;

namespace SafetyLab.Cluster;

/// <summary>
/// What is actually running, against what the assignment table says is assigned.
///
/// Deliberately dumb: this is the fallback measurement for when the streaming capture and the
/// checker pipeline are themselves under suspicion, which in this rig they repeatedly have been.
///
/// The counts and the verdict travel together as one value, so nothing downstream re-derives the
/// verdict from the counts.
/// </summary>
public static class Divergence
{
    public const string Clean = "clean";
    public const string Duplicate = "DUPLICATE";
    public const string Diverged = "DIVERGED";

    /// <summary>One agent, where it is really running, and where the table says it should be.</summary>
    public sealed record Finding(string Agent, IReadOnlyList<string> RunningOn, string TableSays);

    public sealed record Result(
        int Running,
        int Assigned,
        IReadOnlyList<Finding> Duplicated,
        IReadOnlyList<Finding> Orphaned,
        IReadOnlyList<Finding> Missing)
    {
        [JsonIgnore] public int DuplicatedCount => Duplicated.Count;
        [JsonIgnore] public int OrphanedCount => Orphaned.Count;
        [JsonIgnore] public int MissingCount => Missing.Count;

        /// <summary>
        /// Spelled exactly as results.tsv has always spelled them. A duplicate outranks the rest
        /// because it is the user-visible bug: one agent, two live copies.
        /// </summary>
        public string Verdict =>
            DuplicatedCount > 0 ? Duplicate :
            OrphanedCount > 0 || MissingCount > 0 ? Diverged :
            Clean;

        public bool Diverges => Verdict != Clean;

        /// <summary>report.txt's first line, byte-identical to what runs/ and evidence/ already carry.</summary>
        public string SummaryLine =>
            $"running={Running} assigned={Assigned} duplicated={DuplicatedCount} " +
            $"orphaned={OrphanedCount} missing={MissingCount}";
    }

    /// <param name="running">(pod, agentUri) — one entry per agent actually running, per pod.</param>
    /// <param name="assigned">(agentUri, pod) — one entry per assignment row; pod is the node description.</param>
    public static Result Compare(
        IEnumerable<(string Pod, string Agent)> running,
        IEnumerable<(string Agent, string Pod)> assigned)
    {
        var runningRows = running.ToList();
        var assignedRows = assigned.ToList();

        var podsByAgent = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (pod, agent) in runningRows)
        {
            if (!podsByAgent.TryGetValue(agent, out var pods))
            {
                pods = new SortedSet<string>(StringComparer.Ordinal);
                podsByAgent[agent] = pods;
            }

            pods.Add(pod);
        }

        // Last row wins, as the shell-era version did. The assignment table
        // has a primary key on (agent), so a second row for one agent cannot happen on PostgreSQL;
        // on RavenDB a split key would show up as S1 in the checkers, not here.
        var assignedPod = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (agent, pod) in assignedRows) assignedPod[agent] = pod;

        var duplicated = new List<Finding>();
        var orphaned = new List<Finding>();

        foreach (var (agent, pods) in podsByAgent.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var says = assignedPod.GetValueOrDefault(agent, "<unassigned>");

            if (pods.Count > 1)
            {
                duplicated.Add(new Finding(agent, pods.ToList(), says));
            }
            else if (!pods.Contains(assignedPod.GetValueOrDefault(agent, "")))
            {
                orphaned.Add(new Finding(agent, pods.ToList(), says));
            }
        }

        var missing = assignedPod.Keys
            .Where(agent => !podsByAgent.ContainsKey(agent))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(agent => new Finding(agent, [], assignedPod[agent]))
            .ToList();

        return new Result(runningRows.Count, assignedRows.Count, duplicated, orphaned, missing);
    }

    /// <summary>
    /// report.txt's detail block, in the shape it has always had.
    /// </summary>
    public static IEnumerable<string> VerboseLines(Result result)
    {
        foreach (var (label, group) in new[] { ("DUPLICATED", result.Duplicated), ("ORPHANED", result.Orphaned) })
        {
            foreach (var finding in group)
            {
                yield return $"  {label} {finding.Agent} running on [{string.Join(", ", finding.RunningOn)}] " +
                             $"table says [{finding.TableSays}]";
            }
        }

        foreach (var finding in result.Missing)
        {
            yield return $"  MISSING {finding.Agent} assigned to [{finding.TableSays}] but running nowhere";
        }
    }
}
