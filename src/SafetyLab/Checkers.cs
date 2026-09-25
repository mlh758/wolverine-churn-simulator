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
/// <c>EndedByRestart</c> marks a residency that no AGENT-STOP closed: the pod announced a new node
/// identity, so the process that held the agent had died, and <c>End</c> is that announcement.
/// </summary>
public record Residency(string AgentUri, string PodName, DateTimeOffset Start, DateTimeOffset? End,
    bool EndedByRestart = false)
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
            StoppedAndNeverReplaced(history, options, residencies),
            ReplicaAgreement(history, options),
            NoDocumentConflicts(history),
            Convergence(history, options, residencies),
            PartitionTook(history),
            LockHolderIsAlive(history, options),
            LockKillTook(history),
            DbCutTook(history),
            Coverage(history)
        ];
    }

    /// <summary>The marks a partition run writes, and P1 reads. Spelled once.</summary>
    public const string PartitionStartMark = "partition-start";

    public const string PartitionHealMark = "partition-heal";

    /// <summary>
    /// The marks a lock-session run writes, and K1 reads. One per ARM, because the two arms
    /// expect opposite outcomes: terminate takes the lock away, cancel proves an interrupted
    /// connection does not. A single mark made K1 fail every correct control run and say "the
    /// session was never terminated" about a run that never tried to terminate one.
    /// </summary>
    public const string LockKillMark = "lock-kill";

    public const string LockCancelMark = "lock-cancel";

    /// <summary>
    /// The marks an app-to-store partition run writes (E9), and K2 reads. Deliberately NOT
    /// <see cref="PartitionStartMark"/>: that one means "a cut between RavenDB cluster members",
    /// which P1 proves by a member losing its Raft leader. Cutting an app node from its database
    /// is a structurally different fault with different evidence — there are no Raft members to
    /// ask — and sharing one label would put P1 red on every correct run of it.
    /// </summary>
    public const string DbCutStartMark = "db-cut-start";

    public const string DbCutHealMark = "db-cut-heal";

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
            : $"two sessions holding one {history.SessionLockNoun} is impossible in {history.StoreName}, so " +
              "this is a sentinel: if it trips, the lock id is wrong and every leader check below is vacuous");

        return new CheckResult("S1", "At most one holder of the leader lock", violations, notes);
    }

    /// <summary>
    /// Enforced by the store on every backend — a primary key on
    /// <c>wolverine_node_assignments.id</c> on the RDBMS arms, document identity on the
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
        // from the pod logs. On the RDBMS arms it needs the identity map to turn a client address
        // into a node, and without it the 'same node' half cannot run at all.
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
        // RavenDB's lock names its own owner, so this runs with no pod logs at all. The RDBMS
        // arms cannot: pg_stat_activity / performance_schema.threads know an address, nothing more.
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
    /// An RDBMS leadership lock is released by the death of the session holding it, which is
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
                $"skipped: {history.StoreName} {history.SessionLockNoun}s have no expiration — the death of " +
                "the holding session is the release, so there is no window for this to describe"
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

        // The capture's window opens at its first sample. The pod side is harvested whole -- a
        // pod alive at capture start contributes its log from its first line, because the replay
        // needs the AGENT-STARTs from before the run to know what was running when the run
        // began -- so the logs can carry an overlap from an earlier disturbance, hours old, that
        // this run's verdict must not be red for. It is not dropped: it is counted in a note.
        // With no sample at all there is no window, and everything is reported, as `overlaps` does.
        var runStart = history.Good.FirstOrDefault()?.Ts;
        var violations = new List<Violation>();
        var before = 0;

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

                    if (runStart is { } start && to <= start)
                    {
                        before++;
                        continue;
                    }

                    violations.Add(new Violation(from, to,
                        $"{a.AgentUri} ran on both {a.PodName} and {b.PodName} for {(to - from).TotalSeconds:F1}s"));
                }
            }
        }

        var notes = new List<string>
        {
            $"AGENT-START/STOP timestamps come from each pod's own clock; overlaps shorter than " +
            $"{options.CrossPodGrace.TotalSeconds:F0}s are treated as skew and ignored",
            "pods deleted before their logs were captured contribute no residencies — see the coverage check"
        };

        if (before > 0)
        {
            notes.Add($"{before} overlap(s) ended before this capture's first sample at {runStart:HH:mm:ss} " +
                      "and are not reported here: the pod logs are harvested whole, so an earlier " +
                      "disturbance's duplicates are in them, but they are not this run's finding");
        }

        notes.AddRange(RestartNote(residencies));

        return new CheckResult("S5", "No agent runs on two nodes at once", Order(violations), notes);
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

        return new CheckResult("S7", "Every running agent is assigned to the node running it", violations,
            RestartNote(residencies));
    }

    /// <summary>
    /// An agent that was running, was stopped, and was then placed nowhere at all — the dual of
    /// S5/S6/S7 (GH-4590), which none of them can see because the agent has left both sets they
    /// quantify over. Keyed on the STOP rather than on a count of unplaced agents, because a
    /// capacity-constrained run withholds agents by design and L1 already counts those; and only
    /// for a node still in the cluster at the end, because an agent whose node died is the
    /// ungraceful-death shortfall, which follower-kill.sh measures. Conservative both ways: not
    /// judged inside the final convergence window, not judged if the node left later. The history
    /// is the 2026-09-19 and 2026-09-23 entries in RESULTS.md and the shed-nowhere fixture.
    /// </summary>
    private static CheckResult StoppedAndNeverReplaced(RunHistory history, CheckOptions options,
        IReadOnlyList<Residency> residencies)
    {
        var samples = history.Good.ToArray();

        if (residencies.Count == 0 || history.Identities.Count == 0 || samples.Length == 0)
        {
            return new CheckResult("S12", "An agent stopped on a node that stayed in the cluster is placed again", [],
                ["skipped: needs identity records, AGENT-START/STOP events and at least one good sample"])
            {
                Skipped = true
            };
        }

        var runEnd = samples[^1].Ts;
        var tailFrom = runEnd - options.ConvergenceWindow;
        var tail = samples.Where(x => x.Ts >= tailFrom).ToArray();

        var notes = new List<string>
        {
            $"judged over the last {options.ConvergenceWindow.TotalSeconds:F0}s of the run; an agent " +
            "stopped inside that window had no time to be re-placed and is not counted"
        };

        if (tail.Length < 2)
        {
            notes.Add("the run is shorter than one convergence window, so there is no tail to judge");
            return new CheckResult("S12", "An agent stopped on a node that stayed in the cluster is placed again", [],
                notes) { Skipped = true };
        }

        // Still registered when the run ended. A node that left is the other property.
        var stillInCluster = samples[^1].Nodes.Select(x => x.Id).ToHashSet();

        // Placed anywhere at any point in the tail — one sample is enough to say it came back.
        var placedInTail = tail.SelectMany(RunHistory.SimAssignments).Select(x => x.Id).ToHashSet();

        var violations = new List<Violation>();

        foreach (var group in residencies.GroupBy(x => x.AgentUri))
        {
            if (placedInTail.Contains(group.Key)) continue;

            // Still running somewhere, or only stopped inside the tail: not judged.
            if (group.Any(x => x.End is null || x.End.Value > tailFrom)) continue;

            var last = group.OrderBy(x => x.Start).Last();

            // A stint the process's death ended (the pod announced a new node id) is a node that
            // LEFT, whatever the pod's name still says: the other property. And the node to judge
            // by is the one in effect when the stint began, not the pod's latest identity, which
            // after a restart is the survivor -- that read every victim's agent as shed by a node
            // that stayed, on any capture spanning a kill.
            if (last.EndedByRestart) continue;

            var node = history.Identities
                .LastOrDefault(x => x.PodName == last.PodName && x.Ts <= last.Start)?.NodeId;
            if (node is null || !stillInCluster.Contains(node.Value)) continue;

            violations.Add(new Violation(last.End!.Value, runEnd,
                $"{group.Key} was stopped on {last.PodName} at {last.End!.Value:HH:mm:ss}, which was still in " +
                $"the cluster at the end of the run, and was never placed again " +
                $"({(runEnd - last.End!.Value).TotalSeconds:F0}s running nowhere)"));
        }

        if (violations.Count == 0)
        {
            notes.Add("agents stopped because their node left the cluster are not counted here — " +
                      "that is the ungraceful-death shortfall, which follower-kill.sh measures");
        }

        return new CheckResult("S12", "An agent stopped on a node that stayed in the cluster is placed again",
            Order(violations), notes);
    }

    // ----------------------------------------------------------- replication

    /// <summary>
    /// Replicated RavenDB only, and the assignment half of E7.
    ///
    /// Agent assignments are written with a plain session — a single-member write with
    /// asynchronous multi-master replication — and GH-4407's claim-if-absent guard is enforced by
    /// the member serving the request, not across the cluster. So under a partition two members
    /// can each accept a claim on the same agent and both succeed. From outside, that is two
    /// members holding different owners for one agent uri, and this reports exactly that: any
    /// member whose assignment set differs from the primary's for longer than the grace window.
    /// Replication lag on a healthy cluster is milliseconds, so anything the grace window lets
    /// through is the store having two answers, not one answer arriving late.
    /// </summary>
    private static CheckResult ReplicaAgreement(RunHistory history, CheckOptions options)
    {
        if (!history.IsReplicated)
        {
            return new CheckResult("S9", "Every store member agrees on assignment ownership", [],
            [
                history.IsRavenDb
                    ? "skipped: single-member RavenDB capture — there is no second copy of the assignment set to disagree"
                    : "skipped: PostgreSQL is a single node; the assignment table has one copy"
            ])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            var disagreeing = (sample.Replicas ?? [])
                .Where(r => r.Error is null && (r.Divergent.Count > 0 || r.Missing.Count > 0))
                .ToArray();

            if (disagreeing.Length == 0) return null;

            var worst = disagreeing.OrderByDescending(r => r.Divergent.Count + r.Missing.Count).First();
            var example = worst.Divergent.FirstOrDefault();
            var shown = example is null
                ? $"{worst.Missing.Count} agent(s) the primary places that it has no document for"
                : $"e.g. {example.Id} owned by node {example.NodeId} there";

            return $"{disagreeing.Length} member(s) disagree with the primary view; " +
                   $"{RunHistory.ShortNode(worst.Url)} differs on {worst.Divergent.Count} row(s) and lacks " +
                   $"{worst.Missing.Count} ({shown})";
        });

        var everCompared = history.Good.Any(s => s.Replicas is { Count: > 1 } && s.Replicas.Count(r => r.Error is null) > 1);
        var notes = new List<string>
        {
            $"primary view is {RunHistory.ShortNode(history.ReplicaUrls.FirstOrDefault() ?? "?")}; every other " +
            "member is compared against it row by row on every tick"
        };

        if (!everCompared)
        {
            notes.Add("no tick ever had two readable members, so nothing here was actually compared — read C0");
        }

        return new CheckResult("S9", "Every store member agrees on assignment ownership", violations, notes);
    }

    /// <summary>
    /// Replicated RavenDB only. A document conflict is the store's own statement that two members
    /// accepted incompatible writes to one document — for <c>AgentAssignments</c>, two claims on
    /// one agent. Zero grace, because a conflict that a resolver clears within a tick is still a
    /// conflict that happened; the 1s sampling is the floor on what this can see, not a reason to
    /// wait. Read alongside the run's <c>conflicts.tsv</c>, which the partition script takes
    /// after the heal and which also lists conflicts already resolved.
    /// </summary>
    private static CheckResult NoDocumentConflicts(RunHistory history)
    {
        if (!history.IsReplicated)
        {
            return new CheckResult("S10", "No store member reports document conflicts", [],
                ["skipped: conflicts need two members accepting writes; this capture had one store endpoint"])
            {
                Skipped = true
            };
        }

        var violations = Condense(history.Good, TimeSpan.Zero, sample =>
        {
            var conflicted = (sample.Replicas ?? []).Where(r => r.Conflicts is > 0).ToArray();
            if (conflicted.Length == 0) return null;

            return string.Join(", ", conflicted.Select(r =>
                $"{RunHistory.ShortNode(r.Url)} reports {r.Conflicts} conflicted document(s)"));
        });

        var everCounted = history.Good.Any(s => (s.Replicas ?? []).Any(r => r.Conflicts is not null));
        var notes = new List<string>
        {
            "neither Wolverine nor this rig configures a conflict resolver, so a conflict that never shows here " +
            "may have been resolved by RavenDB's default within one tick — the post-heal conflicts.tsv lists " +
            "resolved-conflict revisions too"
        };

        if (!everCounted)
        {
            notes.Add("no member ever answered with a conflict count, so nothing here was exercised");
        }

        return new CheckResult("S10", "No store member reports document conflicts", violations, notes);
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

    /// <summary>
    /// The leadership lock is held by a node that is still HEARTBEATING.
    ///
    /// <see cref="OrphanedLeaderLock"/> (S4) catches a lock held by a node that has left the
    /// registry. This catches the case that one cannot: a node that is still registered, still
    /// listed, still owns the leader row — and has stopped writing to the database at all.
    ///
    /// That is what an app-to-store partition does on PostgreSQL, and it is worth a check of its
    /// own precisely because nothing else in the store shows it. The session holding the advisory
    /// lock stays alive on the server (it is the client that cannot reach it), so the lock is
    /// still held and no peer can take it. The assignment table is frozen mid-health, which reads
    /// as fully placed. The node that would eject a stale registration is the leader, and the
    /// leader is the one that is cut off. So every other check here passes over a cluster that has
    /// stopped making progress — the same "nothing in the store shows it" shape the RavenDB
    /// five-minute stall had, arriving by a different route.
    ///
    /// Grace-windowed like the rest: ChurnSim heartbeats every 2 s, so a tick or two of an
    /// unchanged <c>health_check</c> is sampling, not silence.
    /// </summary>
    private static CheckResult LockHolderIsAlive(RunHistory history, CheckOptions options)
    {
        const string title = "The leader lock is not held by a node that stopped heartbeating";

        if (history.IsRavenDb)
        {
            return new CheckResult("S11", title, [],
            [
                "skipped: RavenDB's leadership lock is a document that names its owner and carries an " +
                "expiry, so a holder that stopped writing is S8's stall rather than this"
            ])
            { Skipped = true };
        }

        // Last time each node's health_check was observed to CHANGE. A value that never moves is
        // the signal; the absolute value is a database clock and the sample's is the monitor's,
        // so the two are never compared to each other (docs/harness-traps.md, two clocks).
        var lastChange = new Dictionary<Guid, DateTimeOffset>();
        var lastSeen = new Dictionary<Guid, DateTimeOffset>();

        var violations = Condense(history.Good, options.Grace, sample =>
        {
            foreach (var node in sample.Nodes)
            {
                if (!lastSeen.TryGetValue(node.Id, out var previous) || previous != node.HealthCheck)
                {
                    lastChange[node.Id] = sample.Ts;
                }

                lastSeen[node.Id] = node.HealthCheck;
            }

            var holder = history.LeaderHolders(sample).FirstOrDefault();
            if (holder?.NodeId is not { } nodeId) return null;

            // A holder that is no longer registered is S4's, not this one.
            if (sample.Nodes.All(x => x.Id != nodeId)) return null;
            if (!lastChange.TryGetValue(nodeId, out var changed)) return null;

            var silent = sample.Ts - changed;
            return silent >= options.Grace
                ? $"node {nodeId} ({holder.Where}) holds {history.LeaderLockDescription} but its health_check " +
                  $"has not moved for {silent.TotalSeconds:F0}s — it is still registered, so nothing will eject " +
                  "it, and its session still holds the lock, so no peer can take it"
                : null;
        });

        var everHeld = history.Good.Any(s => history.LeaderHolders(s).Any(x => x.NodeId is not null));
        var notes = new List<string>();
        if (!everHeld)
        {
            notes.Add("no attributable lock holder was observed in this run, so this check had nothing to " +
                      "examine — on PostgreSQL attribution needs the identity records in pods.jsonl");
        }

        return new CheckResult("S11", title, violations, notes);
    }

    // -------------------------------------------------------------- nemesis

    /// <summary>
    /// The fault injector must prove it fired (docs/harness-traps.md). A partition run writes
    /// <c>partition-start</c> and <c>partition-heal</c> marks; between them at least one member
    /// must have reported LOSING its Raft leader — that is what a minority member does when it
    /// can no longer reach a majority, and it is the one thing the store itself says about being
    /// cut off. A run whose members all saw a leader throughout was not partitioned, whatever the
    /// firewall rules said, and every finding in it is a finding about an intact cluster.
    ///
    /// Skipped, not passed, when the marks are absent: a rollout capture has no partition to
    /// prove.
    /// </summary>
    private static CheckResult PartitionTook(RunHistory history)
    {
        var start = history.Marks.FirstOrDefault(m => m.Label == PartitionStartMark);
        var heal = history.Marks.LastOrDefault(m => m.Label == PartitionHealMark);

        if (start is null)
        {
            return new CheckResult("P1", "The partition took: a member lost its Raft leader while cut off", [],
                [$"skipped: no '{PartitionStartMark}' mark — this run injected no partition"])
            {
                Skipped = true
            };
        }

        if (!history.IsReplicated)
        {
            return new CheckResult("P1", "The partition took: a member lost its Raft leader while cut off",
            [
                new Violation(start.Ts, heal?.Ts ?? start.Ts,
                    "a partition mark is present but the capture read a single store endpoint, so no member's " +
                    "view of the cut was recorded at all")
            ], []);
        }

        var end = heal?.Ts ?? history.Good.LastOrDefault()?.Ts ?? start.Ts;
        var window = history.Good.Where(s => s.Ts >= start.Ts && s.Ts <= end).ToArray();
        var notes = new List<string>();

        if (heal is null)
        {
            notes.Add($"no '{PartitionHealMark}' mark — the window runs to the end of the capture");
        }

        // Per member: how long it reported no Raft leader inside the window.
        var leaderless = new Dictionary<string, int>();
        var unreadable = new Dictionary<string, int>();

        foreach (var sample in window)
        {
            foreach (var replica in sample.Replicas ?? [])
            {
                var name = RunHistory.ShortNode(replica.Url);
                if (replica.Error is not null)
                {
                    unreadable[name] = unreadable.GetValueOrDefault(name) + 1;
                    continue;
                }

                if (string.IsNullOrEmpty(replica.RaftLeader))
                {
                    leaderless[name] = leaderless.GetValueOrDefault(name) + 1;
                }
            }
        }

        var tick = history.Meta is { TickMs: > 0 } meta ? meta.TickMs / 1000.0 : 1.0;

        foreach (var (name, ticks) in leaderless.OrderBy(x => x.Key))
        {
            notes.Add($"{name} reported no Raft leader for ~{ticks * tick:F0}s of the {(end - start.Ts).TotalSeconds:F0}s window");
        }

        foreach (var (name, ticks) in unreadable.OrderBy(x => x.Key))
        {
            notes.Add($"{name} was unreadable by the monitor for ~{ticks * tick:F0}s of the window — the monitor " +
                      "is meant to sit on neither side of the cut; if this is the isolated member, the rig cut " +
                      "the observer too");
        }

        var violations = new List<Violation>();
        if (leaderless.Count == 0)
        {
            violations.Add(new Violation(start.Ts, end,
                window.Length == 0
                    ? "no samples fall inside the partition window, so the cut was not observed at all"
                    : "no member ever reported losing its Raft leader during the partition window — the cut " +
                      "did not take, and this run measured an intact cluster"));
        }

        return new CheckResult("P1", "The partition took: a member lost its Raft leader while cut off",
            violations, notes);
    }

    /// <summary>
    /// The other nemesis that must prove it fired — E8's lock-session kill, and the PostgreSQL
    /// counterpart of <see cref="PartitionTook"/>.
    ///
    /// The fault is <c>pg_terminate_backend</c> on the backend holding the leadership advisory
    /// lock: the session dies, and because a session-level advisory lock has no expiry and no
    /// owner column, its death <em>is</em> the release. (MySQL's <c>KILL</c> is the same fault on
    /// the same shape of lock, but no script injects it yet, so a MySQL capture simply never
    /// carries the mark and this check skips.) The node is told nothing, which is the
    /// whole point. But the statement returning true proves only that a signal was delivered, and
    /// there are several ways for the run to be measuring an undisturbed cluster anyway — the
    /// resolved pid belonged to some other connection, the monitor was watching the wrong lock id
    /// (the id is <c>schemaName.GetDeterministicHashCode()</c>, and watching 9999999 finds
    /// nothing on every tick), or the capture started after the kill. In all of them every other
    /// check in this run passes over a cluster that never lost its lock.
    ///
    /// So the assertion is made against the capture rather than the injector: a backend was
    /// observed holding the lock before the mark, and that same pid is not still holding it
    /// afterwards. A new pid is a fired fault too — the leader reconnecting is a new session, and
    /// what the experiment measures is what the cluster did in between.
    ///
    /// Skipped, not passed, when the mark is absent: a rollout capture has no lock kill to prove.
    /// </summary>
    private static CheckResult LockKillTook(RunHistory history)
    {
        var title = "The lock-session kill took: the advisory lock left the backend holding it";

        var mark = history.Marks.FirstOrDefault(m => m.Label is LockKillMark or LockCancelMark);
        if (mark is null)
        {
            return new CheckResult("K1", title, [],
                [$"skipped: no '{LockKillMark}' or '{LockCancelMark}' mark — this run signalled no lock session"])
            {
                Skipped = true
            };
        }

        // The cancel arm's whole claim is the opposite one, so it is a different assertion rather
        // than the same one with the sign flipped in prose.
        var cancelled = mark.Label == LockCancelMark;
        if (cancelled) title = "The cancelled connection kept its lock: an interrupted statement is not a lost lock";

        if (history.IsRavenDb)
        {
            return new CheckResult("K1", title,
            [
                new Violation(mark.Ts, mark.Ts,
                    "a lock-kill mark is present on a RavenDB capture. RavenDB's leadership lock is a " +
                    "compare-exchange document with an expiry and no session to terminate, so whatever this " +
                    "run injected, it was not this fault — see S8 for the RavenDB shape")
            ], []);
        }

        var samples = history.Good.ToArray();
        var before = samples.LastOrDefault(s => s.Ts <= mark.Ts && history.LeaderLockHolders(s).Any());

        if (before is null)
        {
            return new CheckResult("K1", title,
            [
                new Violation(samples.FirstOrDefault()?.Ts ?? mark.Ts, mark.Ts,
                    $"no sample before the signal observed {history.LeaderLockDescription} held at all, so there " +
                    "is nothing to show it was taken away. Either the capture started after the kill, or the " +
                    "monitor is watching the wrong lock id — in which case every leader check in this run is " +
                    "vacuous, and S1's sentinel note says the same thing")
            ], []);
        }

        var victim = history.LeaderLockHolders(before).First().Pid;
        var after = samples.Where(s => s.Ts > mark.Ts).ToArray();

        if (after.Length == 0)
        {
            return new CheckResult("K1", title,
            [
                new Violation(mark.Ts, mark.Ts,
                    $"pid {victim} held {history.LeaderLockDescription} at the signal and the capture ends there — " +
                    "nothing was observed afterwards, so the run says nothing about what the fault did")
            ], []);
        }

        var notes = new List<string>();
        var released = after.FirstOrDefault(s => history.LeaderLockHolders(s).All(x => x.Pid != victim));

        // ---- the cancel arm: the lock is supposed to stay exactly where it was.
        if (cancelled)
        {
            if (released is null)
            {
                notes.Add($"pid {victim} kept {history.LeaderLockDescription} for the whole " +
                          $"{(after[^1].Ts - mark.Ts).TotalSeconds:F0}s after its statement was cancelled — the " +
                          "control arm behaved as designed, so a lock that DID move in the terminate arm moved " +
                          "because the session died and not because the connection was disturbed");
                return new CheckResult("K1", title, [], notes);
            }

            return new CheckResult("K1", title,
            [
                new Violation(mark.Ts, released.Ts,
                    $"pid {victim} lost {history.LeaderLockDescription} " +
                    $"{(released.Ts - mark.Ts).TotalSeconds:F1}s after its statement was cancelled. The SERVER " +
                    "keeps the session and its lock through a cancellation, so something on the client side " +
                    "dropped the connection in response — that is a finding about the node, and it also means " +
                    "this run is not the control arm it was filed as")
            ], notes);
        }

        // ---- the terminate arm: the lock must have left the backend that held it.
        if (released is null)
        {
            return new CheckResult("K1", title,
            [
                new Violation(mark.Ts, after[^1].Ts,
                    $"pid {victim} still holds {history.LeaderLockDescription} for the whole {(after[^1].Ts - mark.Ts).TotalSeconds:F0}s " +
                    "after the kill. The session was never terminated, so this run measured an undisturbed cluster")
            ], notes);
        }

        notes.Add($"pid {victim} lost {history.LeaderLockDescription} {(released.Ts - mark.Ts).TotalSeconds:F1}s " +
                  "after the mark");

        // How long nothing held it, and who has it now. Both are the measurement rather than the
        // check: on PostgreSQL the lock is free the instant the session dies, so this window is
        // the cluster's own detect-and-reacquire time and not a property of the store.
        var reclaimed = after.FirstOrDefault(s => s.Ts >= released.Ts && history.LeaderLockHolders(s).Any());

        if (reclaimed is null)
        {
            notes.Add($"NOBODY took the lock for the remaining {(after[^1].Ts - released.Ts).TotalSeconds:F0}s of " +
                      "the capture — the cluster was left leaderless by a single terminated connection, which is " +
                      "the finding this experiment is for. Read it with S3 (does the leader row still name the " +
                      "old node?) and L1 (did it ever converge?)");
        }
        else
        {
            var holder = history.LeaderLockHolders(reclaimed).First();
            var who = history.NodeForAddress(holder.ClientAddr, reclaimed.Ts) is { } node
                ? $"node {node}" + (history.PodForNode(node) is { } pod ? $" ({pod})" : "")
                : "a backend the identity map cannot place";

            notes.Add($"the lock was unheld for {(reclaimed.Ts - released.Ts).TotalSeconds:F1}s, then taken by " +
                      $"pid {holder.Pid} = {who}");
        }

        return new CheckResult("K1", title, [], notes);
    }

    /// <summary>
    /// E9's nemesis proving it fired: an app-to-store partition, PostgreSQL.
    ///
    /// P1 cannot answer this. Its evidence is a RavenDB member reporting the loss of its Raft
    /// leader, and there is no Raft here — the fault is a firewall rule between one application
    /// pod and the database. The database-side evidence is different and better: a node that
    /// cannot reach the store stops writing its heartbeat, so its <c>health_check</c> stops
    /// advancing while every peer's keeps moving. That is visible from outside with no cooperation
    /// from the cut-off node, which is the whole point of watching from the store.
    ///
    /// What this does NOT assert is that the cut-off node kept the lock — that is the measurement
    /// (and S11's violation), not the proof, and folding it in here would put K2 red on a
    /// successful experiment. K2 answers one question: did any node actually lose the database?
    /// </summary>
    private static CheckResult DbCutTook(RunHistory history)
    {
        const string title = "The app-to-store cut took: a node stopped heartbeating while it was cut off";

        var start = history.Marks.FirstOrDefault(m => m.Label == DbCutStartMark);
        if (start is null)
        {
            return new CheckResult("K2", title, [],
                [$"skipped: no '{DbCutStartMark}' mark — this run cut nothing off from the store"])
            {
                Skipped = true
            };
        }

        if (history.IsRavenDb)
        {
            return new CheckResult("K2", title,
            [
                new Violation(start.Ts, start.Ts,
                    "an app-to-store cut mark is present on a RavenDB capture. This experiment is the " +
                    "PostgreSQL one; the RavenDB store partition is E7, and P1 is the check that proves it")
            ], []);
        }

        var heal = history.Marks.LastOrDefault(m => m.Label == DbCutHealMark);
        var end = heal?.Ts ?? history.Good.LastOrDefault()?.Ts ?? start.Ts;
        var window = history.Good.Where(x => x.Ts >= start.Ts && x.Ts <= end).ToArray();
        var notes = new List<string>();

        if (heal is null)
        {
            notes.Add($"no '{DbCutHealMark}' mark — the window runs to the end of the capture");
        }

        if (window.Length == 0)
        {
            return new CheckResult("K2", title,
            [
                new Violation(start.Ts, end,
                    "no samples fall inside the cut window, so the partition was not observed at all")
            ], notes);
        }

        // Per node, the longest stretch inside the window over which health_check never moved.
        var lastValue = new Dictionary<Guid, DateTimeOffset>();
        var silentSince = new Dictionary<Guid, DateTimeOffset>();
        var longest = new Dictionary<Guid, TimeSpan>();

        foreach (var sample in window)
        {
            foreach (var node in sample.Nodes)
            {
                if (lastValue.TryGetValue(node.Id, out var previous) && previous == node.HealthCheck)
                {
                    var since = silentSince.TryGetValue(node.Id, out var from) ? from : sample.Ts;
                    silentSince[node.Id] = since;
                    var run = sample.Ts - since;
                    if (!longest.TryGetValue(node.Id, out var best) || run > best) longest[node.Id] = run;
                }
                else
                {
                    silentSince.Remove(node.Id);
                }

                lastValue[node.Id] = node.HealthCheck;
            }
        }

        // ChurnSim heartbeats every 2s (Durability.HealthCheckPollingTime), so anything beyond a
        // few cadences is silence rather than sampling. Deliberately generous: this decides
        // whether a run counts at all, and a false "it fired" is worse than a re-run.
        var threshold = TimeSpan.FromSeconds(5);
        var silent = longest.Where(x => x.Value >= threshold).OrderByDescending(x => x.Value).ToList();

        foreach (var (nodeId, duration) in silent)
        {
            var pod = history.PodForNode(nodeId) is { } name ? $" ({name})" : "";
            notes.Add($"node {nodeId}{pod} stopped heartbeating for {duration.TotalSeconds:F0}s of the " +
                      $"{(end - start.Ts).TotalSeconds:F0}s window");
        }

        // Who held the lock while the cut was on, and did it ever move? This is the measurement
        // the experiment exists for; S11 is what turns it red.
        var holders = window
            .SelectMany(x => history.LeaderHolders(x))
            .Select(x => x.NodeId)
            .Where(x => x is not null)
            .Distinct()
            .ToList();

        notes.Add(holders.Count switch
        {
            0 => "nothing held the leadership lock at any point inside the window",
            1 => $"the leadership lock never moved during the cut — node {holders[0]} held it throughout" +
                 (silent.Any(x => x.Key == holders[0])
                     ? ", and that is the node that stopped heartbeating: no peer could take a lock whose " +
                       "session is still alive on the server, and no node ejects a registration that is " +
                       "still listed. Read S11 and L1"
                     : ""),
            _ => $"the leadership lock changed hands {holders.Count - 1} time(s) during the cut"
        });

        if (silent.Count == 0)
        {
            return new CheckResult("K2", title,
            [
                new Violation(start.Ts, end,
                    "every registered node kept heartbeating for the whole window, so no node lost the " +
                    "database. The cut did not take — whatever the firewall rules said — and this run " +
                    "measured an intact cluster")
            ], notes);
        }

        return new CheckResult("K2", title, [], notes);
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

        // A member the monitor could not read is a hole in that member's column of the history,
        // and S9/S10/P1 are silent about what they could not see. The observer is meant to sit
        // on neither side of a partition, so on a partition run this is the rig, not the store.
        if (history.IsReplicated)
        {
            notes.Add($"replicated capture: {history.ReplicaUrls.Count} member(s) read every tick, primary " +
                      $"{RunHistory.ShortNode(history.ReplicaUrls.FirstOrDefault() ?? "?")}");

            var memberFailures = history.Good
                .SelectMany(s => s.Replicas ?? [])
                .Where(r => r.Error is not null)
                .GroupBy(r => RunHistory.ShortNode(r.Url))
                .ToArray();

            if (memberFailures.Length > 0)
            {
                var failed = history.Good.Where(s => (s.Replicas ?? []).Any(r => r.Error is not null)).ToArray();
                violations.Add(new Violation(failed[0].Ts, failed[^1].Ts,
                    $"member read failures: " + string.Join(", ", memberFailures.Select(g =>
                        $"{g.Key} ×{g.Count()} (first: {g.First().Error})")) +
                    " — S9/S10/P1 are blind to that member for those ticks"));
            }
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
    /// Says when residencies were closed by a container restart rather than by the log, because
    /// the close time is then an upper bound: the process died somewhere before its replacement
    /// announced itself, and a duplicate shorter than that gap cannot be seen.
    /// </summary>
    private static IReadOnlyList<string> RestartNote(IReadOnlyList<Residency> residencies)
    {
        var ended = residencies.Where(x => x.EndedByRestart).ToList();
        if (ended.Count == 0) return [];

        var pods = string.Join(", ", ended.Select(x => x.PodName).Distinct().Order(StringComparer.Ordinal));
        return
        [
            $"{ended.Count} residenc{(ended.Count == 1 ? "y" : "ies")} on {pods} closed at a container restart " +
            "(the pod announced a new node id; the dead process logged no AGENT-STOP) — the close is the " +
            "new process's SIM-IDENTITY, so the true end is earlier"
        ];
    }

    /// <summary>
    /// Reconstruct per-pod agent residencies from the log stream. An AGENT-START with no matching
    /// AGENT-STOP stays open; an AGENT-STOP with no matching start is dropped, which is what a
    /// capture that began after the agent did looks like.
    ///
    /// <para>
    /// A pod that announces a <b>different</b> node id has a new process in it: a SIGKILL restarts
    /// the container inside the same pod, the follower keeps appending to the same pods.jsonl, and
    /// the dead process never logged its AGENT-STOPs. Everything still open on that pod is closed at
    /// the announcement. Without this, every agent the corpse held and the leader re-placed read as
    /// running in two places until the capture ended. The same node id again is only the follower
    /// re-reading the log after re-attaching, and changes nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Residency> BuildResidencies(RunHistory history)
    {
        var open = new Dictionary<(string Agent, string Pod), DateTimeOffset>();
        var closed = new List<Residency>();
        var nodeOnPod = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // Identities sort ahead of agent events at the same instant: a new process announces
        // itself before it starts anything, so a tie belongs to the new process.
        var timeline = history.Identities.Select(x => (x.Ts, Order: 0, Identity: (IdentityRecord?)x, Agent: (AgentEventRecord?)null))
            .Concat(history.AgentEvents.Select(x => (x.Ts, Order: 1, Identity: (IdentityRecord?)null, Agent: (AgentEventRecord?)x)))
            .OrderBy(x => x.Ts).ThenBy(x => x.Order);

        foreach (var (_, _, identity, e) in timeline)
        {
            if (identity is not null)
            {
                if (nodeOnPod.TryGetValue(identity.PodName, out var previous) && previous != identity.NodeId)
                {
                    foreach (var key in open.Keys.Where(k => k.Pod == identity.PodName).ToList())
                    {
                        open.Remove(key, out var began);
                        closed.Add(new Residency(key.Agent, key.Pod, began, identity.Ts, EndedByRestart: true));
                    }
                }

                nodeOnPod[identity.PodName] = identity.NodeId;
                continue;
            }

            var agentKey = (e!.AgentUri, e.PodName);

            if (e.Event == "start")
            {
                // A second start with no intervening stop: keep the earlier one, which is the
                // conservative reading for an exclusivity check.
                if (!open.ContainsKey(agentKey)) open[agentKey] = e.Ts;
            }
            else if (open.Remove(agentKey, out var start))
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
