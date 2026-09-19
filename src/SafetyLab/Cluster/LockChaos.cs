namespace SafetyLab.Cluster;

/// <summary>
/// The lock-session fault, PostgreSQL only: take the leadership advisory lock away from a leader
/// that still believes it holds it, by killing the backend holding it.
///
/// <b>Why this is the whole fault.</b> Wolverine's PostgreSQL leadership lock is a session-level
/// advisory lock (<c>pg_try_advisory_lock(schemaName.GetDeterministicHashCode())</c>) held on one
/// long-lived dedicated connection. It has no expiry and no owner column: the death of the
/// session <em>is</em> the release. So every way an operator describes losing it — the
/// connection timed out, a pooler bounced, somebody killed the session, a blip interrupted it —
/// arrives at the server as the same event, and the server tells the node nothing. Meanwhile
/// lock-loss detection on the node is a <c>select 1</c> liveness ping that deliberately reports
/// the last known state when its gate is busy, so "I am the leader" is a lagging belief by
/// design. A leader that has lost the lock and does not know it yet is the GH-2602 shape, and
/// this is the cheapest way to put a real cluster into it on demand.
///
/// <b>An operator cannot "remove the lock", and a nemesis built on trying is a lie.</b>
/// <c>pg_advisory_unlock(id)</c> only ever touches the <em>calling</em> session's own lock table:
/// run from psql, or from any other connection, it releases nothing, returns <c>false</c> and
/// emits a WARNING that psql prints to stderr — where <c>2&gt;/dev/null</c> puts it out of sight.
/// The same goes for <c>pg_advisory_unlock_all()</c>. An injector written that way runs its full
/// duration, injects nothing, and leaves a plausible number behind, which is exactly the failure
/// docs/harness-traps.md rule 4 exists for. <see cref="TerminateSql"/> is the only route in from
/// outside, and <see cref="Classify"/> makes the run prove the lock actually moved.
///
/// <b>The control arm is real too.</b> <see cref="CancelSql"/> (<c>pg_cancel_backend</c>)
/// interrupts whatever the session is doing and leaves the session — and therefore the lock —
/// exactly where it was. That is the "blip that did not actually cost anybody leadership" arm,
/// and having it is what makes a finding from the terminate arm mean something.
///
/// Pure: every decision below is a function of captured text, tested in
/// tests/SafetyLab.Tests/LockChaosTests.cs with no cluster. <see cref="LockChaosVerbs"/> is the
/// half that talks to kubectl and psql.
/// </summary>
public static class LockChaos
{
    /// <summary>Kill the session: the lock is released by the server, instantly and silently.</summary>
    public const string Terminate = "terminate";

    /// <summary>Interrupt the session's current statement and leave it — and the lock — alive.</summary>
    public const string Cancel = "cancel";

    /// <summary>One backend's row in <c>pg_locks</c> for the leadership lock id.</summary>
    /// <param name="Granted">
    /// False means this backend is QUEUED behind the holder. A waiter is not a holder, and
    /// terminating one injects no fault at all — <see cref="TryChooseTarget"/> refuses on it.
    /// </param>
    public sealed record Holder(int Pid, bool Granted, string ClientAddr, string ApplicationName, string State)
    {
        public string Describe()
        {
            var where = ClientAddr.Length > 0 ? $" ({ClientAddr})" : " (no client address — a local socket?)";
            var app = ApplicationName.Length > 0 ? $" application_name '{ApplicationName}'" : "";
            return $"pid {Pid}{where}{app}";
        }
    }

    /// <summary>Which node the cluster currently says leads, and where its pod is reachable.</summary>
    public sealed record LeaderPod(string Pod, string Address);

    /// <summary>What actually happened to the lock, read back from the server after the fault.</summary>
    public enum Outcome
    {
        /// <summary>Nothing holds the lock any more. The leader has lost it and has not been told.</summary>
        Released,

        /// <summary>Some session already holds it again — a peer, or the same node on a new connection.</summary>
        TakenOver,

        /// <summary>The same backend still holds it. Whatever was fired, it did not land.</summary>
        Unmoved
    }

    // ------------------------------------------------------------------ the SQL

    /// <summary>
    /// <c>pg_locks</c> is keyed on the lock id in two columns, and both filters below are
    /// load-bearing:
    ///
    /// <c>objid</c> is an unsigned <c>oid</c>, so a schema whose hash is negative appears as its
    /// 2^32 complement. <see cref="ObjId"/> does the same low-32-bit conversion
    /// <c>RunHistory.LeaderLockHolders</c> does, so the injector and the monitor cannot end up
    /// watching two different locks.
    ///
    /// <c>objsubid = 1</c> is the single-key <c>bigint</c> form, which is what Wolverine takes.
    /// The two-key <c>(int, int)</c> form records <c>objsubid = 2</c> and can collide on
    /// <c>objid</c> with a completely unrelated lock; without this filter a busy database could
    /// hand this injector somebody else's session to kill.
    /// </summary>
    public static string HoldersSql(long lockId) =>
        $"""
         select l.pid::text || chr(9) || (case when l.granted then 't' else 'f' end) || chr(9) ||
                coalesce(host(a.client_addr), '') || chr(9) ||
                coalesce(a.application_name, '') || chr(9) || coalesce(a.state, '')
           from pg_locks l
           left join pg_stat_activity a on a.pid = l.pid
          where l.locktype = 'advisory' and l.objsubid = 1 and l.objid = {ObjId(lockId)}
          order by l.granted desc, l.pid;
         """;

    /// <summary>
    /// Who owns <c>wolverine://leader/</c>, as a pod name. <c>like 'wolverine://leader%'</c>
    /// rather than an equality: <c>Uri.ToString()</c> adds the trailing slash and the un-slashed
    /// form was what an earlier version of this rig compared against, finding nothing on every
    /// run (see <c>RunHistory.IsLeaderUri</c>).
    /// </summary>
    public const string LeaderPodSql =
        """
        select coalesce(n.description, '(unregistered node)')
          from wolverine.wolverine_node_assignments a
          left join wolverine.wolverine_nodes n on n.id = a.node_id
         where a.id like 'wolverine://leader%';
        """;

    /// <summary>
    /// Spelled as an explicit <c>'t'</c>/<c>'f'</c> rather than letting the boolean render itself.
    /// The two renderings differ — psql prints a boolean column as <c>t</c>, while
    /// <c>boolean::text</c> is <c>true</c> — and a caller that knows only one of them reads a
    /// delivered signal as unparseable, or worse, an undelivered one as delivered.
    /// <see cref="TryParseSignalled"/> accepts both anyway, because this is not the kind of thing
    /// to be sure about twice.
    /// </summary>
    public static string TerminateSql(int pid)
        => $"select case when pg_terminate_backend({pid}) then 't' else 'f' end;";

    public static string CancelSql(int pid)
        => $"select case when pg_cancel_backend({pid}) then 't' else 'f' end;";

    public static string Sql(string mode, int pid)
        => mode == Cancel ? CancelSql(pid) : TerminateSql(pid);

    /// <summary>The unsigned form of the lock id, as <c>pg_locks.objid</c> stores it.</summary>
    public static uint ObjId(long lockId) => (uint)lockId;

    // ----------------------------------------------------------------- parsing

    /// <summary>
    /// <c>pid TAB granted TAB client_addr TAB application_name TAB state</c>, one backend per
    /// line. A line of any other shape is dropped rather than guessed at: psql writes NOTICE and
    /// WARNING text down the same pipe, and a half-parsed line here becomes a pid handed to
    /// <c>pg_terminate_backend</c>.
    /// </summary>
    public static IReadOnlyList<Holder> ParseHolders(string tsv)
    {
        var holders = new List<Holder>();

        foreach (var line in tsv.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 5) continue;
            if (!int.TryParse(parts[0].Trim(), out var pid)) continue;

            var granted = parts[1].Trim();
            if (granted is not ("t" or "f" or "true" or "false")) continue;

            holders.Add(new Holder(pid, granted is "t" or "true", parts[2].Trim(), parts[3].Trim(),
                parts[4].Trim()));
        }

        return holders;
    }

    /// <summary>
    /// psql's rendering of a boolean scalar. Neither <c>pg_terminate_backend</c> nor
    /// <c>pg_cancel_backend</c> throws when the pid is gone — both answer <c>f</c> — so this is
    /// the difference between a signal that was delivered and one that was sent into a hole.
    /// </summary>
    public static bool TryParseSignalled(string raw, out bool signalled, out string problem)
    {
        signalled = false;
        problem = "";

        var text = raw.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);

        if (text is null)
        {
            problem = "the server answered nothing at all, so whether the signal was delivered is unknown";
            return false;
        }

        if (text is not ("t" or "f" or "true" or "false"))
        {
            problem = $"the server answered '{text}', which is not a boolean — the statement did not run";
            return false;
        }

        signalled = text is "t" or "true";
        return true;
    }

    // ------------------------------------------------------------ the decision

    /// <summary>
    /// The one backend to signal, or a named refusal. Every branch that could end in signalling
    /// the wrong pid — or in signalling nothing while reporting a fault — is its own refusal, for
    /// the same reason <c>PostgresChaos.TryChooseVictim</c> has one per empty case: an injector
    /// that fires into nothing produces a run that reads exactly like a clean one.
    /// </summary>
    /// <param name="leader">
    /// The pod the leader assignment row points at, and its IP. Required: "kill the leader's
    /// connection" is only that experiment if the session being killed is demonstrably the
    /// leader's, and <c>client_addr</c> is the only thing the server knows about who is on the
    /// other end.
    /// </param>
    public static bool TryChooseTarget(IReadOnlyList<Holder> holders, LeaderPod? leader, long lockId,
        out Holder? target, out string problem)
    {
        target = null;
        problem = "";

        if (holders.Count == 0)
        {
            problem = $"no backend holds or waits on advisory lock {lockId} (objid {ObjId(lockId)}). Either the " +
                      "cluster has not elected a leader yet, or the lock id is wrong — it is " +
                      "schemaName.GetDeterministicHashCode(), NOT the unused LeaderLockId = 9999999 constant " +
                      "in the same class, and watching the wrong one finds nothing forever";
            return false;
        }

        var granted = holders.Where(x => x.Granted).ToList();

        if (granted.Count == 0)
        {
            problem = $"{holders.Count} backend(s) are QUEUED on advisory lock {lockId} and none has been " +
                      "granted it. A waiter is not the holder, and terminating one injects no fault";
            return false;
        }

        if (granted.Count > 1)
        {
            // Two granted holders of one advisory lock is impossible in PostgreSQL, so this is the
            // filter matching something it should not rather than a cluster in a strange state.
            // Picking one anyway would kill an unrelated session.
            problem = $"{granted.Count} backends are all granted advisory lock {lockId}, which PostgreSQL does " +
                      "not permit: " + string.Join(", ", granted.Select(x => x.Describe())) +
                      ". The lock id or the pg_locks filter is matching more than one lock; refusing to pick";
            return false;
        }

        var holder = granted[0];

        if (holder.Pid <= 0)
        {
            problem = $"the lock row carries pid {holder.Pid}, which is not a backend pid; refusing to signal it";
            return false;
        }

        if (leader is null)
        {
            problem = "the leader assignment row does not resolve to a live pod with an IP, so there is nothing " +
                      "to check the lock holder against. Killing an unidentified session is not this experiment";
            return false;
        }

        if (holder.ClientAddr.Length == 0)
        {
            problem = $"the lock is held by {holder.Describe()}, and pg_stat_activity has no client address for " +
                      "it, so it cannot be shown to be the leader's connection";
            return false;
        }

        if (!string.Equals(holder.ClientAddr, leader.Address, StringComparison.Ordinal))
        {
            problem = $"the lock is held by {holder.Describe()} but the leader assignment row belongs to " +
                      $"{leader.Pod} at {leader.Address}. The lock and the row already disagree, which is the " +
                      "state this experiment exists to CREATE — the cluster is in it before the fault, so a " +
                      "window timed from here measures a recovery from an unknown starting point. Let it settle " +
                      "(S3 is the check for this) and retry";
            return false;
        }

        target = holder;
        return true;
    }

    /// <summary>
    /// What the lock did, read back from the server rather than inferred from the fact that the
    /// statement returned. <paramref name="after"/> is a fresh <see cref="HoldersSql"/> read.
    ///
    /// <see cref="Outcome.Unmoved"/> is the one that matters: the statement succeeded, the run
    /// would otherwise be filed as a lock-loss measurement, and the lock never moved.
    /// </summary>
    public static Outcome Classify(Holder target, IReadOnlyList<Holder> after, out string detail)
    {
        var granted = after.Where(x => x.Granted).ToList();

        if (granted.Count == 0)
        {
            detail = $"nothing holds the lock — {target.Describe()} is gone and no peer has taken it yet";
            return Outcome.Released;
        }

        if (granted.Any(x => x.Pid == target.Pid))
        {
            detail = $"{target.Describe()} STILL holds the lock";
            return Outcome.Unmoved;
        }

        detail = "the lock is already held again by " + string.Join(", ", granted.Select(x => x.Describe())) +
                 $" — the session that replaced {target.Describe()} may be a peer or the same node reconnecting; " +
                 "the node identity is in the capture, not here";
        return Outcome.TakenOver;
    }

    /// <summary>
    /// Did the arm do what it claims? Split from <see cref="Classify"/> because the two modes
    /// expect opposite outcomes, and reading one mode's success as the other's is how a control
    /// arm quietly becomes a second copy of the treatment arm.
    /// </summary>
    public static NemesisVerdict Verdict(string mode, bool signalled, Outcome outcome, string detail)
    {
        if (!signalled)
        {
            return new NemesisVerdict(false,
                $"the server answered false: there was no such backend to {mode}. Nothing was injected, and " +
                "the pid was resolved moments earlier — the session died on its own in between, so whatever " +
                "follows is not this fault");
        }

        if (mode == Cancel)
        {
            // pg_cancel_backend cancels the running statement and the server keeps the session,
            // and so the lock (checked against 17.7). A lock that moved anyway is therefore a
            // statement about the CLIENT — Npgsql surfaces a cancellation as an exception, and
            // whether Wolverine's lock connection survives one is exactly the sort of thing
            // nobody has tested. That is a finding, and it is emphatically not a control arm.
            return outcome == Outcome.Unmoved
                ? new NemesisVerdict(true,
                    $"the statement was cancelled and the lock did not move ({detail}) — the control arm " +
                    "behaved as designed: an interrupted connection is not a lost lock")
                : new NemesisVerdict(false,
                    $"the statement was cancelled, which the SERVER answers by keeping the session and its " +
                    $"lock, and yet {detail}. Something on the client side dropped the connection in response " +
                    "to the cancellation — worth chasing, but this run is not the control arm it was launched " +
                    "as, and the terminate arm has nothing to be compared against");
        }

        return outcome == Outcome.Unmoved
            ? new NemesisVerdict(false,
                $"pg_terminate_backend returned true but {detail}. The leader never lost the lock, so this run " +
                "measured an undisturbed cluster")
            : new NemesisVerdict(true, $"the lock session was terminated and {detail}");
    }
}
