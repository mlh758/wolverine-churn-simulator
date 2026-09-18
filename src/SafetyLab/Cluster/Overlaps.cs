using System.Globalization;

namespace SafetyLab.Cluster;

/// <summary>
/// Every window in which one agent ran on two pods at once, replayed from captured pod logs.
///
/// The question `snapshot` cannot answer. A reconcile sweep can clear a duplicate within a few
/// health-check ticks (~6 s at ChurnSim's 2 s cadence), so the end state reads clean — and clean
/// is indistinguishable from "no duplicate ever happened", which is exactly the distinction a
/// convergence fix has to be judged on. The logs carry every start and stop with a timestamp, so
/// the whole timeline is recoverable after the fact; nothing needs to be sampled.
///
/// Each overlap is classified:
///
///   HEALED     — both copies were running, then one stopped. Convergence works, and the duration
///                is how long the cluster was wrong.
///   PERSISTED  — both copies were still running when the capture ended.
/// </summary>
public static class Overlaps
{
    public const string Never = "never";
    public const string Healed = "HEALED";
    public const string Persisted = "PERSISTED";

    /// <summary>One agent's residency on one pod. A null <paramref name="End"/> never stopped.</summary>
    public sealed record Span(string Pod, DateTimeOffset Start, DateTimeOffset? End);

    public sealed record Overlap(
        string Agent, string PodA, string PodB, DateTimeOffset From, DateTimeOffset To, bool StillOpen)
    {
        public TimeSpan Duration => To - From;
    }

    public sealed record Result(
        int Agents,
        int Events,
        IReadOnlyList<Overlap> Healed,
        IReadOnlyList<Overlap> Persisted)
    {
        /// <summary>
        /// Spelled as results.tsv has always spelled it. PERSISTED outranks HEALED: an iteration
        /// that both healed one duplicate and left another standing is a failure to converge.
        /// </summary>
        public string Outcome =>
            Persisted.Count > 0 ? Overlaps.Persisted :
            Healed.Count > 0 ? Overlaps.Healed :
            Never;

        /// <summary>
        /// How long the worst duplicate survived before the cluster corrected it. "-" when
        /// nothing healed, which is what the results file has always carried for that case.
        /// </summary>
        public string LongestHealSeconds =>
            Healed.Count == 0
                ? "-"
                : Healed.Max(x => x.Duration.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture);

        public string SummaryLine =>
            $"agents={Agents} overlaps_healed={Healed.Count} overlaps_persisted={Persisted.Count}";

        /// <summary>The results.tsv columns in order, so the caller appends rather than parses.</summary>
        public string TsvRow =>
            string.Join('\t', Outcome, Healed.Count, Persisted.Count, LongestHealSeconds);
    }

    /// <summary>
    /// Analyse a captured run directory. Same three-way contract as <see cref="Snapshot"/>:
    ///
    ///   exit 0 — analysed, no overlap ("never")
    ///   exit 1 — analysed, an overlap was found (HEALED or PERSISTED)
    ///   exit 2 — COULD NOT ANALYSE
    ///
    /// Exit 2 matters: "no timeline" must not be reported as "no duplicate".
    /// </summary>
    public static int Run(string directory, TimeSpan grace, bool tsv)
    {
        if (!Directory.Exists(directory))
        {
            return Refuse($"no such directory: {directory}");
        }

        var files = Directory.GetFiles(directory, "raw.*.jsonl").OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            return Refuse($"no raw.*.jsonl in {directory} — capture the pod logs first");
        }

        var events = new List<(DateTimeOffset Ts, bool Start, string Agent, string Pod)>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var pod = name["raw.".Length..^".jsonl".Length];
            events.AddRange(Events(path, pod));
        }

        var result = Find(events, grace);

        if (result.Events == 0)
        {
            // No timeline at all, which must never be reported as "no duplicate".
            return Refuse($"not one timestamped agent event across {files.Count} pod log(s) in " +
                          $"{directory}; there is no residency timeline to analyse");
        }

        File.WriteAllLines(Path.Combine(directory, "overlaps.txt"),
            new[] { result.SummaryLine }.Concat(VerboseLines(result)));

        Console.WriteLine(tsv ? result.TsvRow : result.SummaryLine);

        return result.Outcome == Never ? 0 : 1;
    }

    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"overlaps: REFUSED — {problem}");
        Console.Error.WriteLine("overlaps: nothing was analysed. This is not evidence of a healthy cluster.");
        return 2;
    }

    /// <summary>
    /// Timestamped events from one pod's log. Events with no clock reading are dropped — guessing
    /// one would invent an overlap or erase one.
    /// </summary>
    public static IEnumerable<(DateTimeOffset Ts, bool Start, string Agent, string Pod)> Events(
        string path, string pod)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (!AgentResidency.TryParseEvent(line, out var e)) continue;
            if (e.Timestamp is not { } ts) continue;

            yield return (ts, e.Start, e.AgentUri, pod);
        }
    }

    /// <summary>
    /// Replay events into residency intervals and find every cross-pod overlap.
    ///
    /// Overlaps at or below <paramref name="grace"/> are ignored: pod clocks differ, and a handover
    /// that briefly runs both copies while the stop is in flight is the protocol working, not a
    /// duplicate.
    /// </summary>
    public static Result Find(
        IEnumerable<(DateTimeOffset Ts, bool Start, string Agent, string Pod)> events, TimeSpan grace)
    {
        var ordered = events.OrderBy(x => x.Ts).ToList();
        if (ordered.Count == 0) return new Result(0, 0, [], []);

        // An interval with no stop is open to the end of the capture, and the end of the capture is
        // the last event anyone logged — not "now", which would stretch every open interval by
        // however long the analysis was left sitting.
        var captureEnd = ordered[^1].Ts;

        var openAt = new Dictionary<(string Agent, string Pod), DateTimeOffset>();
        var residencies = new Dictionary<string, List<Span>>(StringComparer.Ordinal);

        void Add(string agent, Span span)
        {
            if (!residencies.TryGetValue(agent, out var spans))
            {
                spans = [];
                residencies[agent] = spans;
            }

            spans.Add(span);
        }

        foreach (var (ts, start, agent, pod) in ordered)
        {
            var key = (agent, pod);

            if (start)
            {
                // A second START with no STOP between is the same residency continuing, not a new
                // one. Keeping the earlier timestamp is what makes the interval the whole time the
                // agent was up on that pod.
                openAt.TryAdd(key, ts);
            }
            else if (openAt.Remove(key, out var opened))
            {
                Add(agent, new Span(pod, opened, ts));
            }
        }

        foreach (var (key, start) in openAt) Add(key.Agent, new Span(key.Pod, start, null));

        var healed = new List<Overlap>();
        var persisted = new List<Overlap>();

        foreach (var (agent, spans) in residencies.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));

            for (var i = 0; i < spans.Count; i++)
            {
                for (var j = i + 1; j < spans.Count; j++)
                {
                    var a = spans[i];
                    var b = spans[j];
                    if (a.Pod == b.Pod) continue;

                    var from = a.Start > b.Start ? a.Start : b.Start;
                    var aEnd = a.End ?? captureEnd;
                    var bEnd = b.End ?? captureEnd;
                    var to = aEnd < bEnd ? aEnd : bEnd;

                    if (to - from <= grace) continue;

                    // Still open on BOTH sides is what makes it PERSISTED. One copy stopped means
                    // the cluster corrected itself, however slowly.
                    var stillOpen = a.End is null && b.End is null;
                    var overlap = new Overlap(agent, a.Pod, b.Pod, from, to, stillOpen);

                    (stillOpen ? persisted : healed).Add(overlap);
                }
            }
        }

        return new Result(residencies.Count, ordered.Count, healed, persisted);
    }

    /// <summary>
    /// Worst first, pods abbreviated to their last five characters — one rollout's pods share a
    /// ReplicaSet prefix, so only the suffix distinguishes them.
    /// </summary>
    public static IEnumerable<string> VerboseLines(Result result, int limit = 25)
    {
        foreach (var (label, group) in new[] { (Persisted, result.Persisted), (Healed, result.Healed) })
        {
            foreach (var o in group.OrderByDescending(x => x.Duration).Take(limit))
            {
                yield return $"  {label} {o.Agent} on {Tail(o.PodA)}+{Tail(o.PodB)} " +
                             $"for {o.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s " +
                             $"from {o.From:HH:mm:ss}";
            }
        }

        static string Tail(string pod) => pod.Length <= 5 ? pod : pod[^5..];
    }
}
