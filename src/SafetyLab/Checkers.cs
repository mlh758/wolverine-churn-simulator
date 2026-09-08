namespace SafetyLab;

public record Violation(DateTimeOffset From, DateTimeOffset To, string Detail)
{
    public TimeSpan Duration => To - From;
}

public record CheckResult(string Id, string Title, IReadOnlyList<Violation> Violations, IReadOnlyList<string> Notes)
{
    public bool Failed => Violations.Count > 0;

    /// <summary>A check that could not run — no identity data, no pod logs — is neither pass nor fail.</summary>
    public bool Skipped { get; init; }
}

/// <summary>
/// One runtime residency of an agent on a pod, reconstructed from AGENT-START / AGENT-STOP.
/// <c>End</c> is null while the agent was still running when the capture ended.
/// </summary>
public record Residency(string AgentUri, string PodName, DateTimeOffset Start, DateTimeOffset? End)
{
    public bool Covers(DateTimeOffset at) => at >= Start && (End is null || at < End);
    public DateTimeOffset EndOr(DateTimeOffset fallback) => End ?? fallback;
}

public sealed class CheckOptions
{
    /// <summary>
    /// How long a divergence must persist before it counts. Leadership handover is not atomic —
    /// the lock is taken before the assignment row is written, the old row is deleted after the
    /// new one appears — so a sample or two of disagreement is the protocol working, not a bug.
    /// What matters is divergence that does not resolve.
    /// </summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Slack for checks that compare timestamps from two different pods' clocks. Unlike Grace this
    /// is not about protocol transients; it is about not reporting clock skew as a safety violation.
    /// </summary>
    public TimeSpan CrossPodGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the cluster must hold a converged state at the end of a run to pass.</summary>
    public TimeSpan ConvergenceWindow { get; init; } = TimeSpan.FromSeconds(60);
}

public static class Checkers
{
    public static IReadOnlyList<CheckResult> RunAll(RunHistory history, CheckOptions options)
    {
        var residencies = BuildResidencies(history);

        return
        [
            LeaderLockUniqueness(history),
            LeaderRowUniqueness(history),
            LeaderCoherence(history, options),
            OrphanedLeaderLock(history, options),
            RuntimeAgentExclusivity(history, options, residencies),
            AssignedButNotRunning(history, options, residencies),
            RunningButNotAssigned(history, options, residencies),
            Convergence(history, options, residencies),
            Coverage(history)
        ];
    }

    // ---------------------------------------------------------------- safety

    /// <summary>
    /// Two backends holding the same advisory lock is impossible in Postgres, so this is a
    /// sentinel: if it ever trips, the monitor is watching the wrong lock id and every other
    /// leader check in this file is worthless. Cheap insurance against a silently green run.
    /// </summary>
    private static CheckResult LeaderLockUniqueness(RunHistory history)
    {
        var violations = Condense(history.Good, TimeSpan.Zero, sample =>
        {
            var holders = history.LeaderLockHolders(sample).ToArray();
            return holders.Length > 1
                ? $"{holders.Length} backends hold leader lock {history.LeaderLockId}: " +
                  string.Join(", ", holders.Select(x => $"pid {x.Pid} ({x.ClientAddr})"))
                : null;
        });

        var everHeld = history.Good.Any(s => history.LeaderLockHolders(s).Any());
        var notes = new List<string>();
        if (!everHeld)
        {
            notes.Add($"the leader lock ({history.LeaderLockId}) was NEVER observed held in this run — " +
                      "either the cluster never elected a leader, or the lock id is wrong and the " +
                      "leader-side checks below are vacuous");
        }

        return new CheckResult("S1", "At most one backend holds the leader advisory lock", violations, notes);
    }

    /// <summary>
    /// Also PK-enforced (<c>wolverine_node_assignments.id</c> is the primary key), so likewise a
    /// sentinel rather than a discovery. Kept because it costs nothing and it pins the assumption.
    /// </summary>
    private static CheckResult LeaderRowUniqueness(RunHistory history)
    {
        var violations = Condense(history.Good, TimeSpan.Zero, sample =>
        {
            var rows = sample.Assignments.Where(x => x.Id == RunHistory.LeaderUri).ToArray();
            return rows.Length > 1 ? $"{rows.Length} '{RunHistory.LeaderUri}' assignment rows" : null;
        });

        return new CheckResult("S2", $"At most one '{RunHistory.LeaderUri}' assignment row", violations,
            ["primary key on wolverine_node_assignments.id already enforces this; kept as a sentinel"]);
    }

    /// <summary>
    /// The two halves of "who is the leader" — the advisory lock and the assignment row — must
    /// agree once a handover has settled. Sustained disagreement in either direction is the
    /// GH-2602 shape: a node that owns the row without owning the lock has no server-side claim
    /// to leadership, and a node holding the lock with no row has taken leadership invisibly.
    /// </summary>
    private static CheckResult LeaderCoherence(RunHistory history, CheckOptions options)
    {
        var notes = new List<string>();
        var haveIdentities = history.Identities.Count > 0;
        if (!haveIdentities)
        {
            notes.Add("no identity records in pods.jsonl — lock-holder-to-node attribution was skipped, " +
                      "so only the presence checks ran, not the 'same node' check");
        }

        var violations = new List<Violation>();

        violations.AddRange(Condense(history.Good, options.Grace, sample =>
        {
            var row = RunHistory.LeaderRow(sample);
            if (row is null) return null;
            return history.LeaderLockHolders(sample).Any()
                ? null
                : $"node {row.NodeId} owns the leader assignment row but no backend holds lock {history.LeaderLockId}";
        }));

        violations.AddRange(Condense(history.Good, options.Grace, sample =>
        {
            var holder = history.LeaderLockHolders(sample).FirstOrDefault();
            if (holder is null) return null;
            return RunHistory.LeaderRow(sample) is null
                ? $"pid {holder.Pid} ({holder.ClientAddr}) holds the leader lock but there is no leader assignment row"
                : null;
        }));

        if (haveIdentities)
        {
            violations.AddRange(Condense(history.Good, options.Grace, sample =>
            {
                var row = RunHistory.LeaderRow(sample);
                var holder = history.LeaderLockHolders(sample).FirstOrDefault();
                if (row is null || holder is null) return null;

                var holderNode = history.NodeForAddress(holder.ClientAddr, sample.Ts);
                if (holderNode is null) return null; // covered by S4

                return holderNode == row.NodeId
                    ? null
                    : $"leader row says node {row.NodeId} but lock {history.LeaderLockId} is held by " +
                      $"node {holderNode} (pid {holder.Pid}, {holder.ClientAddr})";
            }));
        }

        return new CheckResult("S3", "The leader lock and the leader assignment row agree",
            Order(violations), notes);
    }

    /// <summary>
    /// A leader lock still held by a backend belonging to no registered node. This is the
    /// stacking bug's server-side fingerprint: <c>TryAttainLockAsync</c> called once per heartbeat
    /// stacks the session-level lock, the single release on step-down decrements it once, and the
    /// lock stays held with nothing logged — failover simply stalls. From inside the cluster that
    /// is invisible; from <c>pg_locks</c> it is obvious.
    /// </summary>
    private static CheckResult OrphanedLeaderLock(RunHistory history, CheckOptions options)
    {
        if (history.Identities.Count == 0)
        {
            return new CheckResult("S4", "The leader lock is not held by a departed node", [],
                ["skipped: needs identity records in pods.jsonl to map a backend to a node"])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var holder = history.LeaderLockHolders(sample).FirstOrDefault();
            if (holder is null) return null;

            var holderNode = history.NodeForAddress(holder.ClientAddr, sample.Ts);
            if (holderNode is null)
            {
                return $"leader lock held by pid {holder.Pid} at {holder.ClientAddr}, which never " +
                       "announced itself as a node";
            }

            return sample.Nodes.Any(n => n.Id == holderNode)
                ? null
                : $"leader lock held by node {holderNode} (pid {holder.Pid}, {holder.ClientAddr}), which is " +
                  "no longer registered in wolverine_nodes — failover cannot proceed while this holds";
        });

        return new CheckResult("S4", "The leader lock is not held by a departed node", violations, []);
    }

    /// <summary>
    /// The user-visible safety property, and the strongest check here: an agent must never be
    /// running on two nodes at once. Everything else in this file is machinery; this is the thing
    /// a duplicated projection daemon or a doubled exclusive listener actually looks like.
    /// </summary>
    private static CheckResult RuntimeAgentExclusivity(RunHistory history, CheckOptions options,
        IReadOnlyList<Residency> residencies)
    {
        if (residencies.Count == 0)
        {
            return new CheckResult("S5", "No agent runs on two nodes at once", [],
                ["skipped: no AGENT-START/STOP events in pods.jsonl"])
            {
                Skipped = true
            };
        }

        var runEnd = history.Good.LastOrDefault()?.Ts ?? residencies.Max(x => x.EndOr(x.Start));
        var violations = new List<Violation>();

        foreach (var group in residencies.GroupBy(x => x.AgentUri))
        {
            var ordered = group.OrderBy(x => x.Start).ToArray();

            for (var i = 0; i < ordered.Length; i++)
            {
                for (var j = i + 1; j < ordered.Length; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];
                    if (a.PodName == b.PodName) continue;
                    if (b.Start >= a.EndOr(runEnd)) break; // ordered by start; nothing later can overlap a

                    var from = b.Start;
                    var to = Min(a.EndOr(runEnd), b.EndOr(runEnd));
                    if (to - from <= options.CrossPodGrace) continue;

                    violations.Add(new Violation(from, to,
                        $"{a.AgentUri} ran on both {a.PodName} and {b.PodName} for {(to - from).TotalSeconds:F1}s"));
                }
            }
        }

        return new CheckResult("S5", "No agent runs on two nodes at once", Order(violations),
        [
            $"AGENT-START/STOP timestamps come from each pod's own clock; overlaps shorter than " +
            $"{options.CrossPodGrace.TotalSeconds:F0}s are treated as skew and ignored",
            "pods deleted before their logs were captured contribute no residencies — see the coverage check"
        ]);
    }

    /// <summary>
    /// GH-3987's headline symptom, stated directly: the assignment table places an agent on a node
    /// that is not running it. A short window is normal (the row is written before the remote start
    /// is confirmed); one that never closes is the wedge.
    /// </summary>
    private static CheckResult AssignedButNotRunning(RunHistory history, CheckOptions options,
        IReadOnlyList<Residency> residencies)
    {
        if (residencies.Count == 0 || history.Identities.Count == 0)
        {
            return new CheckResult("S6", "Every assigned agent is actually running on its assigned node", [],
                ["skipped: needs both identity records and AGENT-START/STOP events in pods.jsonl"])
            {
                Skipped = true
            };
        }

        var byAgent = residencies.ToLookup(x => x.AgentUri);

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var stranded = new List<string>();

            foreach (var assignment in RunHistory.SimAssignments(sample))
            {
                var pod = history.PodForNode(assignment.NodeId);
                if (pod is null) continue;

                if (!byAgent[assignment.Id].Any(r => r.PodName == pod && r.Covers(sample.Ts)))
                {
                    stranded.Add($"{assignment.Id}@{pod}");
                }
            }

            if (stranded.Count == 0) return null;

            var shown = string.Join(", ", stranded.Take(5));
            var more = stranded.Count > 5 ? $" (+{stranded.Count - 5} more)" : "";
            return $"{stranded.Count} agent(s) assigned but not running: {shown}{more}";
        });

        return new CheckResult("S6", "Every assigned agent is actually running on its assigned node",
            violations, []);
    }

    /// <summary>
    /// The converse, and the more dangerous of the pair: a node running an agent the assignment
    /// table does not place there. One node in that state is an orphan; two are a duplicate, which
    /// is S5. Catching it here names the node before the overlap happens.
    /// </summary>
    private static CheckResult RunningButNotAssigned(RunHistory history, CheckOptions options,
        IReadOnlyList<Residency> residencies)
    {
        if (residencies.Count == 0 || history.Identities.Count == 0)
        {
            return new CheckResult("S7", "Every running agent is assigned to the node running it", [],
                ["skipped: needs both identity records and AGENT-START/STOP events in pods.jsonl"])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var orphans = new List<string>();

            foreach (var residency in residencies.Where(r => r.Covers(sample.Ts)))
            {
                var assignment = sample.Assignments.FirstOrDefault(a => a.Id == residency.AgentUri);
                var assignedPod = assignment is null ? null : history.PodForNode(assignment.NodeId);

                if (assignedPod != residency.PodName)
                {
                    orphans.Add(assignment is null
                        ? $"{residency.AgentUri}@{residency.PodName} (unassigned)"
                        : $"{residency.AgentUri}@{residency.PodName} (assigned to {assignedPod ?? "unknown"})");
                }
            }

            if (orphans.Count == 0) return null;

            var shown = string.Join(", ", orphans.Take(5));
            var more = orphans.Count > 5 ? $" (+{orphans.Count - 5} more)" : "";
            return $"{orphans.Count} agent(s) running where not assigned: {shown}{more}";
        });

        return new CheckResult("S7", "Every running agent is assigned to the node running it", violations, []);
    }

    // -------------------------------------------------------------- liveness

    /// <summary>
    /// Safety checks pass trivially on a cluster that does nothing. This is the other half: after
    /// the last phase marker, does the cluster actually settle — one leader holding the lock, every
    /// agent placed exactly once — and how long did it take?
    /// </summary>
    private static CheckResult Convergence(RunHistory history, CheckOptions options,
        IReadOnlyList<Residency> residencies)
    {
        var samples = history.Good.ToArray();
        if (samples.Length == 0)
        {
            return new CheckResult("L1", "The cluster converges after the last phase marker", [],
                ["skipped: no usable samples"]) { Skipped = true };
        }

        var from = history.Marks.LastOrDefault()?.Ts ?? samples[0].Ts;
        var tail = samples.Where(x => x.Ts >= from).ToArray();
        var notes = new List<string>();

        if (history.Marks.Count == 0)
        {
            notes.Add("no marks.jsonl phase markers — measured convergence over the whole run, " +
                      "which is weaker than measuring it after a known disturbance");
        }

        // Every sim agent the run ever placed. Using the union rather than a configured count keeps
        // the check honest when SIM_AGENT_COUNT is not known to the checker.
        var expected = samples.SelectMany(RunHistory.SimAssignments).Select(x => x.Id).ToHashSet();

        string? converged(Sample sample)
        {
            var row = RunHistory.LeaderRow(sample);
            if (row is null) return "no leader assignment row";

            var holders = history.LeaderLockHolders(sample).ToArray();
            if (holders.Length != 1) return $"{holders.Length} leader lock holders";

            var placed = RunHistory.SimAssignments(sample).Select(x => x.Id).ToHashSet();
            if (!expected.SetEquals(placed))
            {
                return $"{expected.Count - placed.Count} of {expected.Count} sim agents unplaced";
            }

            var live = sample.Nodes.Select(x => x.Id).ToHashSet();
            var dangling = RunHistory.SimAssignments(sample).Count(x => !live.Contains(x.NodeId));
            return dangling > 0 ? $"{dangling} assignment(s) point at an unregistered node" : null;
        }

        // Walk forward for the first point after which the state stays converged.
        DateTimeOffset? settledAt = null;
        string? lastReason = null;
        foreach (var sample in tail)
        {
            var reason = converged(sample);
            if (reason is null)
            {
                settledAt ??= sample.Ts;
            }
            else
            {
                settledAt = null;
                lastReason = reason;
            }
        }

        var runEnd = tail.Length > 0 ? tail[^1].Ts : from;
        var violations = new List<Violation>();

        if (settledAt is null)
        {
            violations.Add(new Violation(from, runEnd,
                $"never converged after the last marker; last obstacle was: {lastReason}"));
        }
        else
        {
            var held = runEnd - settledAt.Value;
            notes.Add($"converged {(settledAt.Value - from).TotalSeconds:F1}s after the last marker, " +
                      $"held for {held.TotalSeconds:F1}s");

            if (held < options.ConvergenceWindow)
            {
                notes.Add($"WARNING: the converged tail is shorter than the required " +
                          $"{options.ConvergenceWindow.TotalSeconds:F0}s window — let the run breathe longer " +
                          "before trusting this as a pass");
            }
        }

        if (residencies.Count > 0)
        {
            var openAtEnd = residencies.Count(x => x.End is null);
            notes.Add($"{openAtEnd} agent residenc(ies) were still open when capture ended");
        }

        return new CheckResult("L1", "The cluster converges after the last phase marker", violations, notes);
    }

    // -------------------------------------------------------------- coverage

    /// <summary>
    /// What the run actually saw. Without this, "0 violations" is an unqualified claim over an
    /// unknown amount of observation — the exact way a safety harness lies to you.
    /// </summary>
    private static CheckResult Coverage(RunHistory history)
    {
        var notes = new List<string>();
        var violations = new List<Violation>();

        if (history.Samples.Count == 0)
        {
            return new CheckResult("C0", "Observation coverage", [], ["no samples at all — nothing was checked"])
            {
                Skipped = true
            };
        }

        var tick = TimeSpan.FromMilliseconds(history.Meta?.TickMs ?? 200);
        var threshold = tick * 3;
        var first = history.Samples[0].Ts;
        var last = history.Samples[^1].Ts;

        notes.Add($"{history.Samples.Count} samples over {(last - first).TotalMinutes:F1} min at a " +
                  $"{tick.TotalMilliseconds:F0}ms tick");

        var errors = history.Samples.Count(x => x.Error is not null);
        if (errors > 0)
        {
            notes.Add($"{errors} sample(s) failed outright; their first error was: " +
                      history.Samples.First(x => x.Error is not null).Error);
        }

        for (var i = 1; i < history.Samples.Count; i++)
        {
            var gap = history.Samples[i].Ts - history.Samples[i - 1].Ts;
            if (gap > threshold)
            {
                violations.Add(new Violation(history.Samples[i - 1].Ts, history.Samples[i].Ts,
                    $"{gap.TotalSeconds:F1}s blind spot — nothing was observed here, so no check covers it"));
            }
        }

        // Monitor-clock vs database-clock offset. Wolverine stamps health_check with the database's
        // now() but computes staleness against the *node's* wall clock, so an offset between the two
        // is not cosmetic — it shifts every stale-node decision in the cluster.
        var offsets = history.Good
            .Where(x => x.DbTs is not null)
            .Select(x => (x.DbTs!.Value - x.Ts).TotalMilliseconds)
            .ToArray();

        if (offsets.Length > 0)
        {
            notes.Add($"monitor-to-database clock offset: min {offsets.Min():F0}ms, " +
                      $"max {offsets.Max():F0}ms, mean {offsets.Average():F0}ms " +
                      "(includes query latency, so treat it as an upper bound on true skew)");
        }

        notes.Add($"{history.Identities.Count} node identity record(s), " +
                  $"{history.AgentEvents.Count} agent event(s), {history.Marks.Count} phase marker(s)");

        return new CheckResult("C0", "Observation coverage", violations, notes);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Reconstruct per-pod agent residencies from the log stream. An AGENT-START with no matching
    /// AGENT-STOP stays open; an AGENT-STOP with no matching start is dropped, which is what a
    /// capture that began after the agent did looks like.
    /// </summary>
    public static IReadOnlyList<Residency> BuildResidencies(RunHistory history)
    {
        var open = new Dictionary<(string Agent, string Pod), DateTimeOffset>();
        var closed = new List<Residency>();

        foreach (var e in history.AgentEvents.OrderBy(x => x.Ts))
        {
            var key = (e.AgentUri, e.PodName);

            if (e.Event == "start")
            {
                // A second start with no intervening stop: keep the earlier one, which is the
                // conservative reading for an exclusivity check.
                if (!open.ContainsKey(key)) open[key] = e.Ts;
            }
            else if (open.Remove(key, out var start))
            {
                closed.Add(new Residency(e.AgentUri, e.PodName, start, e.Ts));
            }
        }

        closed.AddRange(open.Select(kv => new Residency(kv.Key.Agent, kv.Key.Pod, kv.Value, null)));
        return closed.OrderBy(x => x.Start).ToList();
    }

    /// <summary>
    /// Collapse a per-sample predicate into maximal intervals where it held, dropping any shorter
    /// than <paramref name="grace"/>. Transients are the normal texture of a handover; what a
    /// checker should report is the divergence that does not end.
    /// </summary>
    private static IReadOnlyList<Violation> Condense(IEnumerable<Sample> samples, TimeSpan grace,
        Func<Sample, string?> probe)
    {
        var violations = new List<Violation>();
        DateTimeOffset? start = null;
        DateTimeOffset lastBad = default;
        string? detail = null;

        foreach (var sample in samples)
        {
            var problem = probe(sample);

            if (problem is not null)
            {
                start ??= sample.Ts;
                detail ??= problem;
                lastBad = sample.Ts;
            }
            else if (start is not null)
            {
                // Ends at the first clean sample, so a single-tick blip has a real duration
                // rather than a zero-length one.
                emit(sample.Ts);
            }
        }

        if (start is not null) emit(lastBad);

        return violations;

        void emit(DateTimeOffset end)
        {
            if (end - start!.Value >= grace)
            {
                violations.Add(new Violation(start.Value, end, detail!));
            }

            start = null;
            detail = null;
        }
    }

    private static IReadOnlyList<Violation> Order(IEnumerable<Violation> violations)
        => violations.OrderByDescending(x => x.Duration).ToList();

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
