using SafetyLab;
using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The preconditions for killing the leader's lock session, and the proof that it fired.
///
/// Each way this fault can inject nothing while reporting success has its own test:
/// <c>pg_terminate_backend</c> answers <c>false</c> rather than throwing when the pid has gone, a
/// waiter queued on the lock is indistinguishable from a holder unless <c>granted</c> is read, and
/// <c>objid</c> is unsigned so a negative schema hash matches nothing when compared as signed.
/// </summary>
public class LockChaosTests
{
    private const string LeaderAddr = "10.244.0.11";
    private const string PeerAddr = "10.244.0.12";

    private static readonly LockChaos.LeaderPod Leader = new("churnsim-a", LeaderAddr);

    private static string Rows(params (int Pid, string Granted, string Addr)[] rows)
        => string.Join("\n", rows.Select(r =>
            $"{r.Pid}\t{r.Granted}\t{r.Addr}\twolverine\tidle")) + "\n";

    // ------------------------------------------------------------- target selection

    [Fact]
    public void the_target_is_the_backend_that_holds_the_lock_from_the_leaders_address()
    {
        var holders = LockChaos.ParseHolders(Rows((4821, "t", LeaderAddr)));

        Assert.True(LockChaos.TryChooseTarget(holders, Leader, 832201495, out var target, out _));
        Assert.Equal(4821, target!.Pid);
    }

    [Fact]
    public void a_lock_nobody_holds_is_refused_with_the_lock_id_named()
    {
        // The usual cause is the wrong lock id: leadership runs on
        // schemaName.GetDeterministicHashCode(), not the LeaderLockId = 9999999 constant declared
        // beside it, which the leadership path does not use. The refusal names both.
        Assert.False(LockChaos.TryChooseTarget([], Leader, 9999999, out var target, out var problem));

        Assert.Null(target);
        Assert.Contains("9999999", problem);
        Assert.Contains("GetDeterministicHashCode", problem);
    }

    [Fact]
    public void a_backend_merely_queued_on_the_lock_is_never_the_target()
    {
        // pg_locks lists waiters alongside the holder. Terminating a waiter succeeds and injects
        // nothing: the leader keeps the lock throughout.
        var holders = LockChaos.ParseHolders(Rows((4830, "f", PeerAddr), (4831, "f", PeerAddr)));

        Assert.False(LockChaos.TryChooseTarget(holders, Leader, 832201495, out _, out var problem));
        Assert.Contains("QUEUED", problem);
    }

    [Fact]
    public void the_holder_is_preferred_over_the_waiters_rather_than_the_first_row()
    {
        var holders = LockChaos.ParseHolders(Rows((4830, "f", PeerAddr), (4821, "t", LeaderAddr)));

        Assert.True(LockChaos.TryChooseTarget(holders, Leader, 832201495, out var target, out _));
        Assert.Equal(4821, target!.Pid);
    }

    [Fact]
    public void two_granted_holders_are_refused_rather_than_picked_between()
    {
        // PostgreSQL cannot grant one advisory lock to two backends, so this means the filter is
        // matching some other lock, and picking either row would signal an unrelated session.
        var holders = LockChaos.ParseHolders(Rows((4821, "t", LeaderAddr), (4900, "t", PeerAddr)));

        Assert.False(LockChaos.TryChooseTarget(holders, Leader, 832201495, out _, out var problem));
        Assert.Contains("does not permit", problem);
    }

    [Fact]
    public void an_unresolved_leader_pod_is_refused_rather_than_killing_whoever_holds_the_lock()
    {
        var holders = LockChaos.ParseHolders(Rows((4821, "t", LeaderAddr)));

        Assert.False(LockChaos.TryChooseTarget(holders, null, 832201495, out _, out var problem));
        Assert.Contains("not this experiment", problem);
    }

    [Fact]
    public void a_lock_held_from_somewhere_other_than_the_leaders_pod_is_refused()
    {
        // The lock and the leader row already disagree, which is the state this experiment exists
        // to create. Firing into it would measure a recovery from an unknown starting point.
        var holders = LockChaos.ParseHolders(Rows((4900, "t", PeerAddr)));

        Assert.False(LockChaos.TryChooseTarget(holders, Leader, 832201495, out _, out var problem));
        Assert.Contains("already disagree", problem);
        Assert.Contains("churnsim-a", problem);
    }

    [Fact]
    public void a_holder_with_no_client_address_cannot_be_shown_to_be_the_leader()
    {
        var holders = LockChaos.ParseHolders(Rows((4821, "t", "")));

        Assert.False(LockChaos.TryChooseTarget(holders, Leader, 832201495, out _, out var problem));
        Assert.Contains("client address", problem);
    }

    [Fact]
    public void a_notice_line_never_becomes_a_pid()
    {
        // psql writes NOTICE and WARNING down the same pipe as the rows, and a pid parsed out of
        // one would go straight into pg_terminate_backend().
        var holders = LockChaos.ParseHolders(
            "NOTICE:  relation already exists\n\nnot-a-pid\tt\t10.0.0.1\tx\tidle\n4821\tt\t" +
            LeaderAddr + "\twolverine\tidle\n");

        Assert.Single(holders);
        Assert.Equal(4821, holders[0].Pid);
    }

    [Fact]
    public void a_row_whose_granted_column_is_not_a_boolean_is_dropped()
    {
        // Rather than defaulting to "granted", which would let a waiter be chosen as the holder.
        Assert.Empty(LockChaos.ParseHolders($"4821\tmaybe\t{LeaderAddr}\twolverine\tidle\n"));
    }

    [Fact]
    public void both_spellings_of_a_postgres_boolean_are_read()
    {
        // psql prints a boolean column as `t`; `boolean::text` is `true`. A parser that knew only
        // one spelling would drop every row if a cast were added, and an empty holder list reads
        // as "the cluster has no leader" rather than as an error.
        Assert.True(LockChaos.ParseHolders($"4821\ttrue\t{LeaderAddr}\twolverine\tidle\n")[0].Granted);
        Assert.False(LockChaos.ParseHolders($"4821\tfalse\t{LeaderAddr}\twolverine\tidle\n")[0].Granted);

        Assert.True(LockChaos.TryParseSignalled("true\n", out var signalled, out _));
        Assert.True(signalled);
    }

    [Fact]
    public void the_queries_ask_for_the_spelling_the_parser_expects()
    {
        // The SQL spells the boolean 't'/'f' itself rather than letting the renderer choose.
        Assert.Contains("then 't' else 'f' end", LockChaos.HoldersSql(832201495));
        Assert.Contains("then 't' else 'f' end", LockChaos.TerminateSql(4821));
        Assert.Contains("then 't' else 'f' end", LockChaos.CancelSql(4821));
    }

    [Fact]
    public void a_pid_that_is_not_a_backend_pid_is_refused()
    {
        var holders = LockChaos.ParseHolders(Rows((0, "t", LeaderAddr)));

        Assert.False(LockChaos.TryChooseTarget(holders, Leader, 832201495, out _, out var problem));
        Assert.Contains("refusing to signal", problem);
    }

    // --------------------------------------------------------------------- the SQL

    [Fact]
    public void the_lock_is_matched_on_the_unsigned_low_32_bits()
    {
        // pg_locks.objid is an oid, so a negative schema hash appears as its 2^32 complement and a
        // signed comparison matches nothing. Same conversion as RunHistory.LeaderLockHolders, so
        // the injector and the monitor cannot watch two different locks.
        Assert.Equal(4294967295u, LockChaos.ObjId(-1));
        Assert.Contains("l.objid = 4294967295", LockChaos.HoldersSql(-1));
        Assert.Contains("l.objid = 832201495", LockChaos.HoldersSql(832201495));
    }

    [Fact]
    public void the_single_key_form_of_the_lock_is_the_one_matched()
    {
        // pg_advisory_lock(bigint) records objsubid 1; the two-key (int, int) form records 2 and
        // can collide on objid with a completely unrelated lock.
        Assert.Contains("l.objsubid = 1", LockChaos.HoldersSql(832201495));
    }

    [Fact]
    public void the_lock_id_is_the_schema_hash_the_monitor_uses()
    {
        // The injector derives the id exactly as RunHistory does, so `lock-chaos kill` and
        // `monitor` cannot disagree about which lock leadership runs on.
        Assert.Equal(832201495, RunHistory.LockIdForSchema("wolverine"));
    }

    [Fact]
    public void the_leader_row_is_matched_with_and_without_the_trailing_slash()
    {
        // Uri.ToString() writes "wolverine://leader/", so an equality test against the unslashed
        // literal would match nothing.
        Assert.Contains("like 'wolverine://leader%'", LockChaos.LeaderPodSql);
    }

    [Fact]
    public void the_two_modes_are_different_statements()
    {
        Assert.Contains("pg_terminate_backend(4821)", LockChaos.Sql(LockChaos.Terminate, 4821));
        Assert.Contains("pg_cancel_backend(4821)", LockChaos.Sql(LockChaos.Cancel, 4821));
    }

    // ------------------------------------------------------------ did it fire?

    [Fact]
    public void a_signal_into_a_hole_answers_false_rather_than_failing()
    {
        // Neither pg_terminate_backend nor pg_cancel_backend throws when the pid has gone: both
        // return false. A caller checking only the exit code would read that as a fired fault.
        Assert.True(LockChaos.TryParseSignalled("f\n", out var signalled, out _));
        Assert.False(signalled);

        var verdict = LockChaos.Verdict(LockChaos.Terminate, signalled, LockChaos.Outcome.Released, "");
        Assert.False(verdict.Valid);
        Assert.Contains("Nothing was injected", verdict.Detail);
    }

    [Fact]
    public void an_unreadable_answer_is_not_read_as_false()
    {
        Assert.False(LockChaos.TryParseSignalled("ERROR:  must be a superuser\n", out _, out var problem));
        Assert.Contains("did not run", problem);

        Assert.False(LockChaos.TryParseSignalled("", out _, out var empty));
        Assert.Contains("nothing at all", empty);
    }

    [Fact]
    public void a_released_lock_is_the_fault_landing()
    {
        var target = new LockChaos.Holder(4821, true, LeaderAddr, "wolverine", "idle");

        Assert.Equal(LockChaos.Outcome.Released, LockChaos.Classify(target, [], out var detail));
        Assert.True(LockChaos.Verdict(LockChaos.Terminate, true, LockChaos.Outcome.Released, detail).Valid);
    }

    [Fact]
    public void a_lock_already_held_again_by_a_new_backend_also_counts_as_fired()
    {
        // The leader reconnects on a new session, so a different pid holding the lock a moment
        // later still means the fault landed.
        var target = new LockChaos.Holder(4821, true, LeaderAddr, "wolverine", "idle");
        var after = LockChaos.ParseHolders(Rows((5107, "t", LeaderAddr)));

        Assert.Equal(LockChaos.Outcome.TakenOver, LockChaos.Classify(target, after, out var detail));
        Assert.True(LockChaos.Verdict(LockChaos.Terminate, true, LockChaos.Outcome.TakenOver, detail).Valid);
    }

    [Fact]
    public void the_same_backend_still_holding_the_lock_invalidates_the_run()
    {
        // The statement returned true, the run looks like a lock-loss measurement, and the leader
        // never lost its lock.
        var target = new LockChaos.Holder(4821, true, LeaderAddr, "wolverine", "idle");
        var after = LockChaos.ParseHolders(Rows((4821, "t", LeaderAddr)));

        Assert.Equal(LockChaos.Outcome.Unmoved, LockChaos.Classify(target, after, out var detail));

        var verdict = LockChaos.Verdict(LockChaos.Terminate, true, LockChaos.Outcome.Unmoved, detail);
        Assert.False(verdict.Valid);
        Assert.Contains("undisturbed cluster", verdict.Detail);
    }

    [Fact]
    public void the_cancel_arm_expects_the_lock_to_survive()
    {
        // pg_cancel_backend interrupts the statement and leaves the session, and so the lock, in
        // place. An interrupted connection is not a lost lock.
        var verdict = LockChaos.Verdict(LockChaos.Cancel, true, LockChaos.Outcome.Unmoved, "the lock did not move");

        Assert.True(verdict.Valid);
        Assert.Contains("control arm", verdict.Detail);
    }

    [Fact]
    public void a_cancel_that_somehow_moved_the_lock_is_not_a_control_arm()
    {
        // The server keeps the session and its lock through a cancellation, so a lock that moved
        // anyway means the client dropped its connection in response. That is a finding about the
        // node, not a control arm.
        var verdict = LockChaos.Verdict(LockChaos.Cancel, true, LockChaos.Outcome.Released, "nothing holds the lock");

        Assert.False(verdict.Valid);
        Assert.Contains("client side dropped the connection", verdict.Detail);
        Assert.Contains("not the control arm", verdict.Detail);
    }
}
