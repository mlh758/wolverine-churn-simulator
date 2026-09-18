using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafetyLab.Cluster;

/// <summary>
/// One running-vs-assigned measurement, taken and judged in a single process.
///
///   exit 0 — measured, nothing diverges
///   exit 1 — measured, something diverges (the counts say what)
///   exit 2 — COULD NOT MEASURE
///
/// Exit 2 is the whole point: "I could not measure" must never be reported as "I measured, and it
/// was clean" (docs/harness-traps.md, rule 1).
/// </summary>
public static class Snapshot
{
    /// <summary>What one pod contributed, so a pod that was never really observed is visible.</summary>
    public sealed record PodObservation(string Pod, int LogLines, int AgentEvents, int Running);

    public sealed record Report(
        string Backend,
        DateTimeOffset TakenUtc,
        IReadOnlyList<PodObservation> Pods,
        int Running,
        int Assigned,
        int Duplicated,
        int Orphaned,
        int Missing,
        string Verdict,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<Divergence.Finding> DuplicatedAgents,
        IReadOnlyList<Divergence.Finding> OrphanedAgents,
        IReadOnlyList<Divergence.Finding> MissingAgents)
    {
        [JsonIgnore] public bool Diverges => Verdict != Divergence.Clean;
    }

    /// <summary>The results.tsv columns in order. Reordering silently mislabels every row written after.</summary>
    public static string TsvRow(Report report)
        => string.Join('\t', report.Verdict, report.Running, report.Assigned,
            report.Duplicated, report.Orphaned, report.Missing);

    /// <summary>
    /// The sentinel: was the thing being watched ever observed AT ALL? An empty running set
    /// against an empty assignment set compares clean, so this has to be caught upstream.
    /// </summary>
    public static bool NothingWasObserved(IReadOnlyList<PodObservation> pods)
        => pods.Count == 0 || pods.Sum(x => x.AgentEvents) == 0;

    public static int Run(string directory, string label, bool tsv)
    {
        var backend = StoreQueries.Backend();
        if (!backend.Ok) return Refuse(backend.Problem);

        var pods = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!pods.Ok) return Refuse($"kubectl could not list pods for '{label}': {pods.StdErr.Trim()}");

        var live = PodSelection.Live(pods.StdOut);
        if (live.Count == 0)
        {
            // Not "nothing is running": nothing could be OBSERVED. Every agent in the table would
            // read as `missing`, which is a divergence report about the harness, not the cluster.
            var all = PodSelection.Parse(pods.StdOut);
            return Refuse(all.Count == 0
                ? $"no pods matched '{label}' at all"
                : $"no LIVE pod among {all.Count} matching '{label}': " +
                  string.Join(", ", all.Select(x =>
                      $"{x.Name} (phase={x.Phase}, ready={x.Ready}, terminating={x.Terminating})")));
        }

        Directory.CreateDirectory(directory);
        ClearPreviousSnapshot(directory);

        var warnings = new List<string>();
        var observations = new List<PodObservation>();
        var running = new List<(string Pod, string Agent)>();

        foreach (var pod in live)
        {
            var path = Path.Combine(directory, $"raw.{pod.Name}.jsonl");
            var logs = ProcessRunner.KubectlToFile(path, "logs", pod.Name);

            if (!logs.Ok)
            {
                // The pod was live when the list was taken and is not readable now, so this
                // snapshot is of a moving cluster. Its agents would be absent from `running` while
                // still present in the table — a false DIVERGED, which is worse than no answer.
                return Refuse($"kubectl logs failed for {pod.Name}, which was live a moment ago: " +
                              $"{logs.StdErr.Trim()}");
            }

            var residency = AgentResidency.Replay(pod.Name, File.ReadLines(path));
            observations.Add(new PodObservation(pod.Name, residency.Lines, residency.Events,
                residency.Running.Count));

            if (residency.Events == 0)
            {
                warnings.Add($"{pod.Name} is live but its log carries no AGENT-START/STOP at all " +
                             $"({residency.Lines} lines) — it may have been captured before it placed anything");
            }

            running.AddRange(residency.Running.Select(agent => (pod.Name, agent)));
        }

        if (NothingWasObserved(observations))
        {
            return Refuse($"not one of the {live.Count} live pod(s) logged a single agent event; " +
                          "there is nothing here to compare against the assignment table");
        }

        var assigned = StoreQueries.Assigned(backend.Value!);
        if (!assigned.Ok) return Refuse($"could not read the assignment table: {assigned.Problem}");

        var result = Divergence.Compare(running, assigned.Value!);

        var report = new Report(
            backend.Value!, DateTimeOffset.UtcNow, observations,
            result.Running, result.Assigned,
            result.DuplicatedCount, result.OrphanedCount, result.MissingCount,
            result.Verdict, warnings,
            result.Duplicated, result.Orphaned, result.Missing);

        Write(directory, running, assigned.Value!, result, report);

        foreach (var warning in warnings) Console.Error.WriteLine($"snapshot: {warning}");

        Console.WriteLine(tsv ? TsvRow(report) : result.SummaryLine);

        return result.Diverges ? 1 : 0;
    }

    /// <summary>
    /// Exit 2, reason on stderr. Nothing is written: a partial snapshot that looks like a whole
    /// one is what this verb exists to prevent.
    /// </summary>
    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"snapshot: REFUSED — {problem}");
        Console.Error.WriteLine("snapshot: no measurement was taken. This is not a clean result.");
        return 2;
    }

    /// <summary>
    /// Stale raw.*.jsonl would attribute a departed pod's agents to this measurement. Only this
    /// verb's own outputs are removed.
    /// </summary>
    private static void ClearPreviousSnapshot(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "raw.*.jsonl")) File.Delete(file);

        foreach (var name in new[] { "running.tsv", "assigned.tsv", "report.txt", "snapshot.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Write(
        string directory,
        IReadOnlyList<(string Pod, string Agent)> running,
        IReadOnlyList<(string Agent, string Pod)> assigned,
        Divergence.Result result,
        Report report)
    {
        // running.tsv and assigned.tsv keep the column order the Python scripts used, because
        // captured runs going back months are laid out this way and a reader should not have to
        // check which era a directory came from.
        File.WriteAllLines(Path.Combine(directory, "running.tsv"),
            running.OrderBy(x => x.Pod, StringComparer.Ordinal)
                .ThenBy(x => x.Agent, StringComparer.Ordinal)
                .Select(x => $"{x.Pod}\t{x.Agent}"));

        File.WriteAllLines(Path.Combine(directory, "assigned.tsv"),
            assigned.Select(x => $"{x.Agent}\t{x.Pod}"));

        File.WriteAllLines(Path.Combine(directory, "report.txt"),
            new[] { result.SummaryLine }.Concat(Divergence.VerboseLines(result)));

        // The machine-readable record, and the one a caller should read. It carries the verdict
        // already decided, so nothing downstream re-derives it from prose.
        File.WriteAllText(Path.Combine(directory, "snapshot.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
    }
}
