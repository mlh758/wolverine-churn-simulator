using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// One test per defect that actually happened, in the same spirit as tests/selftest.sh's mutant
/// ledger: a check nobody has seen fire is decoration, and a bug with no test is a bug that comes
/// back. Every fixture here is real output captured from the running cluster, not invented JSON —
/// the shapes are what bit us, so the shapes are what get asserted against.
/// </summary>
public class RegressionTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    // ----------------------------------------------------------- defect 1: `cut -f2` on "none"

    [Fact]
    public void leaderless_is_a_state_not_a_node_id()
    {
        var state = LeaderState.Parse("none", out var problem);

        Assert.True(state.IsLeaderless);
        Assert.Null(state.NodeIdOrNull);
        Assert.Equal("", problem);
    }

    [Fact]
    public void a_leaderless_sample_is_never_reported_as_a_new_leader()
    {
        // THE defect. The shell version read "none" with `cut -f2`, which returns the whole word
        // when there is no tab, compared it unequal to the victim, and announced a NEW LEADER at
        // t+0 against a cluster that had none -- reporting a five-minute outage as an instant
        // recovery. The measurement was not merely wrong, it was inverted.
        var victim = Guid.Parse("fdfc372e-0a5f-4cd2-9a61-701f7e6127c9");

        var samples = new[]
        {
            new FailoverSample(0, LeaderState.Parse("none", out _), null, 334),
            new FailoverSample(6, LeaderState.Parse("none", out _), null, 500),
            new FailoverSample(12, LeaderState.Parse($"pod-a\t{victim}", out _), null, 500)
        };

        Assert.Null(FailoverTimeline.FirstNewLeader(samples, victim));
    }

    [Fact]
    public void the_victim_still_holding_leadership_is_not_a_new_leader()
    {
        var victim = Guid.NewGuid();
        var samples = new[] { new FailoverSample(5, new LeaderState.Held("pod-a", victim), null, 500) };

        Assert.Null(FailoverTimeline.FirstNewLeader(samples, victim));
    }

    [Fact]
    public void a_genuinely_different_live_leader_is_the_recovery_point()
    {
        var victim = Guid.NewGuid();
        var successor = Guid.NewGuid();

        var samples = new[]
        {
            new FailoverSample(0, new LeaderState.Leaderless(), null, 500),
            new FailoverSample(297, new LeaderState.Held("pod-a", victim), null, 500),
            new FailoverSample(303, new LeaderState.Held("pod-a", successor), null, 374)
        };

        Assert.Equal(303, FailoverTimeline.FirstNewLeader(samples, victim));
    }

    [Fact]
    public void an_unparseable_leader_line_reports_why_instead_of_defaulting_silently()
    {
        var state = LeaderState.Parse("pod-a\tnot-a-guid", out var problem);

        Assert.True(state.IsLeaderless);
        Assert.Contains("does not carry a node id", problem);
    }

    // ------------------------------------------------- defect 2: grep '"pid"' on crictl inspect

    [Fact]
    public void host_pid_comes_from_info_pid_and_not_from_the_first_pid_in_the_document()
    {
        // The captured fixture has "pid": 1 (a namespace descriptor) BEFORE the real "pid": 54444,
        // which is exactly why `grep -m1 '"pid"'` resolved to 1 and the injector ran `kill -9 1`
        // against the node's init.
        var json = Fixture("crictl-inspect.json");

        Assert.Equal(54444, ContainerProbe.HostPidFrom(json));
    }

    [Fact]
    public void the_fixture_still_encodes_the_trap_that_caused_the_bug()
    {
        // Pins the FIXTURE, not the parser. The crictl document is only a regression test for as
        // long as its first "pid" is the namespace descriptor rather than the container process;
        // a well-meaning tidy-up that reordered or dropped that field would leave the test above
        // passing while testing nothing. If this fails, the fixture stopped reproducing the bug.
        var json = Fixture("crictl-inspect.json");
        var firstPid = System.Text.RegularExpressions.Regex.Match(json, @"""pid""\s*:\s*(\d+)");

        Assert.True(firstPid.Success);
        Assert.Equal("1", firstPid.Groups[1].Value);
        Assert.NotEqual(1, ContainerProbe.HostPidFrom(json));
    }

    [Fact]
    public void pid_one_is_refused()
    {
        Assert.False(ContainerProbe.IsSafeToKill(1, "/sbin/init", "ChurnSim", out var problem));
        Assert.Contains("init or invalid", problem);
    }

    [Fact]
    public void an_unresolvable_pid_is_refused_rather_than_guessed()
    {
        Assert.Null(ContainerProbe.HostPidFrom("{\"info\":{}}"));
        Assert.False(ContainerProbe.IsSafeToKill(null, "", "ChurnSim", out var problem));
        Assert.Contains("refusing to guess", problem);
    }

    [Fact]
    public void a_pid_whose_process_is_not_the_target_is_refused()
    {
        Assert.False(ContainerProbe.IsSafeToKill(54444, "/usr/bin/containerd-shim", "ChurnSim", out var problem));
        Assert.Contains("refusing to kill the wrong process", problem);
    }

    [Fact]
    public void the_real_container_process_is_accepted()
    {
        Assert.True(ContainerProbe.IsSafeToKill(54444, "dotnet ChurnSim.dll ", "ChurnSim", out _));
    }

    [Fact]
    public void malformed_inspect_output_does_not_throw()
    {
        Assert.Null(ContainerProbe.HostPidFrom("not json at all"));
    }

    // ------------------------------------------------------ defect 3: `.items[0]` pod selection

    [Fact]
    public void a_terminating_pod_is_not_live_even_though_its_phase_is_running()
    {
        var pods = PodSelection.Parse(Fixture("pods-mid-rollout.json"));
        var terminating = pods.Single(x => x.Name == "churnsim-old-aaaaa");

        Assert.Equal("Running", terminating.Phase);
        Assert.True(terminating.Ready);
        Assert.False(terminating.IsLive);
    }

    [Fact]
    public void a_running_but_unready_pod_is_not_live()
    {
        var pods = PodSelection.Parse(Fixture("pods-mid-rollout.json"));
        Assert.False(pods.Single(x => x.Name == "churnsim-new-bbbbb").IsLive);
    }

    [Fact]
    public void a_completed_pod_is_not_live()
    {
        // This is the one that made `kubectl exec` fail with "cannot exec into a container in a
        // completed pod" mid-measurement, because the selector took .items[0].
        var pods = PodSelection.Parse(Fixture("pods-mid-rollout.json"));
        Assert.False(pods.Single(x => x.Name == "safetylab-old-ddddd").IsLive);
    }

    [Fact]
    public void selecting_a_single_pod_skips_the_dead_ones()
    {
        Assert.True(PodSelection.TrySelectSingle(Fixture("pods-mid-rollout.json"), out var pod, out _));
        Assert.Equal("churnsim-new-ccccc", pod!.Name);
    }

    [Fact]
    public void selecting_from_a_list_with_no_live_pod_explains_rather_than_returning_the_first()
    {
        const string json = """
            { "items": [ { "metadata": { "name": "dead-one" },
                           "status": { "phase": "Failed", "containerStatuses": [ { "ready": false } ] } } ] }
            """;

        Assert.False(PodSelection.TrySelectSingle(json, out var pod, out var problem));
        Assert.Null(pod);
        Assert.Contains("dead-one", problem);
        Assert.Contains("phase=Failed", problem);
    }

    [Fact]
    public void the_real_healthy_cluster_parses_and_every_pod_is_live()
    {
        var pods = PodSelection.Parse(Fixture("pods-healthy.json"));

        Assert.Equal(3, pods.Count);
        Assert.All(pods, p => Assert.True(p.IsLive));
    }

    [Fact]
    public void the_restarted_pod_reports_its_restart_count()
    {
        // Captured after the SIGKILL run: one pod's container was killed and restarted by kubelet.
        var pods = PodSelection.Parse(Fixture("pods-healthy.json"));
        Assert.Contains(pods, p => p.RestartCount > 0);
    }

    // ------------------------------------------- defect 4: a nemesis that did not actually fire

    [Fact]
    public void a_nodestopped_record_during_a_sigkill_run_invalidates_it()
    {
        // `kubectl delete --force --grace-period=0` still delivers SIGTERM, so Wolverine ran
        // shutdown and released its lock. The resulting 6-second "failover" was a clean handover
        // mislabelled as a crash.
        var verdict = NemesisVerdict.Evaluate(ungraceful: true, stoppedBefore: 72, stoppedAfter: 73);

        Assert.False(verdict.Valid);
        Assert.Contains("GRACEFULLY", verdict.Detail);
    }

    [Fact]
    public void an_unchanged_nodestopped_count_confirms_the_kill_was_ungraceful()
    {
        var verdict = NemesisVerdict.Evaluate(ungraceful: true, stoppedBefore: 73, stoppedAfter: 73);

        Assert.True(verdict.Valid);
        Assert.Contains("without running shutdown", verdict.Detail);
    }

    [Fact]
    public void the_graceful_control_arm_expects_a_nodestopped()
    {
        Assert.True(NemesisVerdict.Evaluate(ungraceful: false, 72, 73).Valid);
    }

    // ------------------------------------------------------------------- the headline invariant

    [Fact]
    public void full_placement_while_leaderless_is_counted_because_that_is_what_hides_the_outage()
    {
        // The measured run read placed=500 for the whole five-minute stall while ~126 agents were
        // assigned to a dead node and running nowhere. Anything watching the assignment set alone
        // would have called that a healthy cluster.
        var victim = Guid.NewGuid();

        var samples = new[]
        {
            new FailoverSample(0, new LeaderState.Leaderless(), null, 500),
            new FailoverSample(60, new LeaderState.Held("pod-a", victim), null, 500),
            new FailoverSample(120, new LeaderState.Held("pod-a", victim), null, 500),
            new FailoverSample(303, new LeaderState.Held("pod-a", Guid.NewGuid()), null, 374)
        };

        Assert.Equal(3, FailoverTimeline.SamplesFullyPlacedWhileLeaderless(samples, victim, 500));
    }

    [Fact]
    public void lock_state_parses_a_holder_and_its_expiry()
    {
        var now = DateTimeOffset.Parse("2026-09-18T16:30:00Z");
        var state = LockState.Parse(
            "wolverine/leader/churnsim\tfdfc372e-0a5f-4cd2-9a61-701f7e6127c9\t2026-09-18T16:34:57.0483477+00:00\t299");

        Assert.NotNull(state);
        Assert.Equal("wolverine/leader/churnsim", state!.Key);
        Assert.Equal(Guid.Parse("fdfc372e-0a5f-4cd2-9a61-701f7e6127c9"), state.Holder);
        Assert.Equal(297, state.SecondsToExpiry(now)!.Value, 0);
    }

    [Fact]
    public void an_absent_lock_parses_as_null_rather_than_a_zero_expiry()
    {
        Assert.Null(LockState.Parse("none"));
        Assert.Null(LockState.Parse(""));
    }
}
