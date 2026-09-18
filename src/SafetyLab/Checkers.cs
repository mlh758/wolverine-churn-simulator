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
            ExpiredLeaderLock(history, options),
            RuntimeAgentExclusivity(history, options, residencies),
            AssignedButNotRunning(history, options, residencies),
            RunningButNotAssigned(history, options, residencies),
            Convergence(history, options, residencies),
            Coverage(history)
        ];
    }

    // ---------------------------------------------------------------- safety

    /// <summary>
    /// On PostgreSQL this is a sentinel: two backends holding one advisory lock is impossible, so
    /// if it ever trips the monitor is watching the wrong lock id and every other leader check in
    /// this file is worthless. Cheap insurance against a silently green run.
    ///
    /// On RavenDB it is not a sentinel but a real check, because the lock is not one key. The
    /// message store constructs itself with <c>wolverine/leader</c> and renames the key to
    /// <c>wolverine/leader/&lt;service&gt;</c> when <c>StartScheduledJobs</c> runs, and each key is
    /// an independent compare-exchange value with its own index. Two nodes leading under the two
    /// spellings is a state the compare-exchange primitive does nothing to prevent, and it would
    /// look perfectly healthy from inside either node.
    /// </summary>
    private static CheckResult LeaderLockUniqueness(RunHistory history)
    {
        var violations = Condense(history.Good, TimeSpan.Zero, sample =>
        {
            var holders = history.LeaderHolders(sample);
            return holders.Count > 1
                ? $"{holders.Count} holders of the leader lock: " +
                  string.Join(", ", holders.Select(x => x.Where + (x.NodeId is null ? "" : $" = node {x.NodeId}")))
                : null;
        });

        var everHeld = history.Good.Any(s => history.LeaderHolders(s).Count > 0);
        var notes = new List<string>();
        if (!everHeld)
        {
            notes.Add($"the leader lock ({history.LeaderLockDescription}) was NEVER observed held in this " +
                      "run — either the cluster never elected a leader, or the monitor is watching the wrong " +
                      "key and the leader-side checks below are vacuous");
        }

        notes.Add(history.IsRavenDb
            ? "on RavenDB this is a real check, not a sentinel: the leadership key is renamed from " +
              "'wolverine/leader' to 'wolverine/leader/<service>' once StartScheduledJobs runs, and nothing " +
              "stops the two spellings being held by different nodes"
            : "two backends holding one advisory lock is impossible in Postgres, so this is a sentinel: if " +
              "it trips, the lock id is wrong and every leader check below is vacuous");

        return new CheckResult("S1", "At most one holder of the leader lock", violations, notes);
    }

    /// <summary>
    /// Enforced by the store on both backends — a primary key on
    /// <c>wolverine_node_assignments.id</c> on PostgreSQL, document identity on the
    /// <c>AgentAssignments</c> collection on RavenDB — so likewise a sentinel rather than a
    /// discovery. Kept because it costs nothing and it pins the assumption.
    /// </summary>
    private static CheckResult LeaderRowUniqueness(RunHistory history)
    {
        var violations = Condense(history.Good, TimeSpan.Zero, sample =>
        {
            var rows = sample.Assignments.Where(x => RunHistory.IsLeaderUri(x.Id)).ToArray();
            return rows.Length > 1 ? $"{rows.Length} '{RunHistory.LeaderUri}' assignment rows" : null;
        });

        return new CheckResult("S2", $"At most one '{RunHistory.LeaderUri}' assignment row", violations,
        [
            history.IsRavenDb
                ? "one AgentAssignments document per agent uri already enforces this; kept as a sentinel"
                : "primary key on wolverine_node_assignments.id already enforces this; kept as a sentinel"
        ]);
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

        // On RavenDB the lock document carries its owner's node id, so attribution needs nothing
        // from the pod logs. On PostgreSQL it needs the identity map to turn a client_addr into a
        // node, and without it the 'same node' half cannot run at all.
        var canAttribute = history.IsRavenDb || history.Identities.Count > 0;
        if (!canAttribute)
        {
            notes.Add("no identity records in pods.jsonl — lock-holder-to-node attribution was skipped, " +
                      "so only the presence checks ran, not the 'same node' check");
        }

        if (history.IsRavenDb)
        {
            notes.Add("RavenDB's HasLeadershipLock() reads a local field and its expiry and never asks the " +
                      "server who owns the key, so a disagreement found here is belief-versus-server — the " +
                      "GH-2602 shape — and not a reporting artifact");
        }

        var violations = new List<Violation>();

        violations.AddRange(Condense(history.Good, options.Grace, sample =>
        {
            var row = RunHistory.LeaderRow(sample);
            if (row is null) return null;
            return history.LeaderHolders(sample).Count > 0
                ? null
                : $"node {row.NodeId} owns the leader assignment row but nothing holds " +
                  $"{history.LeaderLockDescription}";
        }));

        violations.AddRange(Condense(history.Good, options.Grace, sample =>
        {
            var holder = history.LeaderHolders(sample).FirstOrDefault();
            if (holder is null) return null;
            return RunHistory.LeaderRow(sample) is null
                ? $"{holder.Where} holds the leader lock but there is no leader assignment row"
                : null;
        }));

        if (canAttribute)
        {
            violations.AddRange(Condense(history.Good, options.Grace, sample =>
            {
                var row = RunHistory.LeaderRow(sample);
                var holder = history.LeaderHolders(sample).FirstOrDefault();
                if (row is null || holder is null) return null;
                if (holder.NodeId is null) return null; // unattributable holder; covered by S4

                return holder.NodeId == row.NodeId
                    ? null
                    : $"leader row says node {row.NodeId} but {history.LeaderLockDescription} is held by " +
                      $"node {holder.NodeId} ({holder.Where})";
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
        // RavenDB's lock names its own owner, so this runs with no pod logs at all. Postgres
        // cannot: pg_stat_activity knows a client_addr and nothing more.
        if (!history.IsRavenDb && history.Identities.Count == 0)
        {
            return new CheckResult("S4", "The leader lock is not held by a departed node", [],
                ["skipped: needs identity records in pods.jsonl to map a backend to a node"])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var holder = history.LeaderHolders(sample).FirstOrDefault();
            if (holder is null) return null;

            if (holder.NodeId is null)
            {
                return $"leader lock held by {holder.Where}, which never announced itself as a node";
            }

            return sample.Nodes.Any(n => n.Id == holder.NodeId)
                ? null
                : $"leader lock held by node {holder.NodeId} ({holder.Where}), which is no longer a " +
                  "registered node — failover cannot proceed while this holds";
        });

        var notes = new List<string>();
        if (history.IsRavenDb)
        {
            notes.Add("on RavenDB this is the expected state for up to five minutes after a leader dies " +
                      "without releasing: the compare-exchange value has no session to die with, so it sits " +
                      "there until it expires and a peer CAS-replaces it. Read this check together with S8, " +
                      "and treat a window shorter than the expiry as the protocol rather than a bug");
        }

        return new CheckResult("S4", "The leader lock is not held by a departed node", violations, notes);
    }

    /// <summary>
    /// RavenDB-only, and the reason the RavenDB arm exists as a separate arm at all.
    ///
    /// A PostgreSQL leadership lock is released by the death of the session holding it, which is
    /// immediate and needs no actor. A RavenDB leadership lock is a compare-exchange document with
    /// an <c>ExpirationTime</c> five minutes out, and nothing in the server clears it: the only
    /// path back to an elected leader is <c>tryTakeOverIfExpiredAsync</c> — a <em>peer</em>
    /// noticing the expiry and CAS-replacing the value. So the failure mode is not "two leaders"
    /// but "no leader, for as long as nobody looks", and it is invisible from inside every node
    /// (the dead one is dead; the live ones see a lock they do not hold).
    ///
    /// What this reports is therefore an expired lock that is <em>still there</em> beyond the
    /// grace window — the stall itself, not the expiry, which is normal and momentary.
    /// </summary>
    private static CheckResult ExpiredLeaderLock(RunHistory history, CheckOptions options)
    {
        if (!history.IsRavenDb)
        {
            return new CheckResult("S8", "The leader lock is not left expired but unclaimed", [],
            [
                "skipped: PostgreSQL advisory locks have no expiration — the death of the holding session " +
                "is the release, so there is no window for this to describe"
            ])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var expired = history.LeaderHolders(sample)
                .Where(x => x.ExpiresAt is not null && x.ExpiresAt < sample.Ts)
                .ToArray();

            if (expired.Length == 0) return null;

            var holder = expired[0];
            var age = sample.Ts - holder.ExpiresAt!.Value;
            return $"{holder.Where} is held by node {holder.NodeId} but expired {age.TotalSeconds:F0}s ago " +
                   "and has not been taken over — no node can become leader until a peer CAS-replaces it";
        });

        var everSeen = history.Good.Any(s => history.LeaderHolders(s).Any(x => x.ExpiresAt is not null));
        var notes = new List<string>();
        if (!everSeen)
        {
            notes.Add("no leader lock with an expiration was observed in this run, so nothing here was " +
                      "actually exercised — check S1's note before reading this as a pass");
        }

        return new CheckResult("S8", "The leader lock is not left expired but unclaimed", violations, notes);
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

            var holders = history.LeaderHolders(sample);
            if (holders.Count != 1) return $"{holders.Count} leader lock holders";

            // On RavenDB a lock that is present but expired is not leadership: the incumbent may
            // be gone and no peer has claimed it yet. Converged has to mean a live claim.
            if (holders[0].ExpiresAt is { } expiry && expiry < sample.Ts)
            {
                return $"the leader lock is present but expired ({(sample.Ts - expiry).TotalSeconds:F0}s ago)";
            }

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

        // Prefer the monitor's declared tick, but fall back to the observed median interval
        // rather than a guess. A capture started with `kubectl logs --since` can miss the meta
        // line entirely, and assuming a faster tick than was really used turns every ordinary
        // interval into a reported "blind spot" -- 1,900 false violations on the first live run.
        var tick = history.Meta is { TickMs: > 0 } meta
            ? TimeSpan.FromMilliseconds(meta.TickMs)
            : ObservedTick(history.Samples);
        var threshold = tick * 3;
        var first = history.Samples[0].Ts;
        var last = history.Samples[^1].Ts;

        notes.Add($"backend '{history.Backend}', leadership watched as {history.LeaderLockDescription}");

        notes.Add($"{history.Samples.Count} samples over {(last - first).TotalMinutes:F1} min at a " +
                  $"{tick.TotalMilliseconds:F0}ms tick" +
                  (history.Meta is null ? " (inferred — no meta record in this capture)" : ""));

        var errors = history.Samples.Count(x => x.Error is not null);
        if (errors > 0)
        {
            notes.Add($"{errors} sample(s) failed outright; their first error was: " +
                      history.Samples.First(x => x.Error is not null).Error);
        }

        // A tick that succeeded but came back short or stale. On RavenDB the assignment set is read
        // through a paged query rather than a SELECT, so a run with more agents than the monitor's
        // page limit would quietly check a prefix of the cluster and report it as healthy. That is
        // precisely the class of harness lie C0 exists to make impossible, so a truncated sample is
        // a violation and not a note.
        var warned = history.Samples.Where(x => x.Warnings is { Count: > 0 }).ToArray();
        if (warned.Length > 0)
        {
            var kinds = warned.SelectMany(x => x.Warnings!).GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} (×{g.Count()})");

            violations.Add(new Violation(warned[0].Ts, warned[^1].Ts,
                $"{warned.Length} sample(s) came back incomplete or stale: {string.Join("; ", kinds)}"));
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
                      "(includes query latency, so treat it as an upper bound on true skew)" +
                      (history.IsRavenDb
                          ? " — RavenDB has no now(), so this comes from the HTTP Date response header and is " +
                            "only accurate to a second; do not read sub-second figures from it"
                          : ""));
        }

        notes.Add($"{history.Identities.Count} node identity record(s), " +
                  $"{history.AgentEvents.Count} agent event(s), {history.Marks.Count} phase marker(s)");

        // Per-pod log coverage. wolverine_nodes.description is the pod name, so the samples
        // themselves name every pod that ever registered -- and any pod with no identity record
        // is one the log capture never attached to. Every pod-side check (S5/S6/S7, and S4's
        // address-to-node attribution) is blind to that pod, so this has to be a violation and
        // not a note: an uncaptured pod once produced a false S4 on a healthy cluster.
        var registered = history.Good
            .SelectMany(x => x.Nodes)
            .Select(x => x.Description)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();

        var captured = history.Identities.Select(x => x.PodName).ToHashSet();
        var uncaptured = registered.Where(x => !captured.Contains(x)).OrderBy(x => x).ToArray();

        if (uncaptured.Length > 0)
        {
            violations.Add(new Violation(first, last,
                $"{uncaptured.Length} pod(s) registered as nodes but were never captured: " +
                string.Join(", ", uncaptured) +
                " — every pod-side check is blind to them"));
        }

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

    /// <summary>Median inter-sample interval — robust to the gaps we are trying to measure.</summary>
    private static TimeSpan ObservedTick(IReadOnlyList<Sample> samples)
    {
        if (samples.Count < 3) return TimeSpan.FromMilliseconds(200);

        var deltas = new List<double>(samples.Count - 1);
        for (var i = 1; i < samples.Count; i++)
        {
            deltas.Add((samples[i].Ts - samples[i - 1].Ts).TotalMilliseconds);
        }

        deltas.Sort();
        return TimeSpan.FromMilliseconds(Math.Max(1, deltas[deltas.Count / 2]));
    }

    private static IReadOnlyList<Violation> Order(IEnumerable<Violation> violations)
        => violations.OrderByDescending(x => x.Duration).ToList();

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
