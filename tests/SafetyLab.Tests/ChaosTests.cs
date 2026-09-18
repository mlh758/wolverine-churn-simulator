using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The fault injector's preconditions, and the configuration check that gates every arm.
///
/// docs/harness-traps.md rule 4: a fault injector must prove it fired. "No violation observed"
/// after a nemesis that silently did nothing is worse than no run at all, because it looks like
/// evidence. Every test here is a way the shell version could inject nothing and report success.
/// </summary>
public class ChaosTests
{
    private const string LeaderId = "11111111-1111-1111-1111-111111111111";
    private const string PeerId = "22222222-2222-2222-2222-222222222222";
    private const string OtherId = "33333333-3333-3333-3333-333333333333";

    private static string Nodes(params (int Number, string Id, string Pod)[] rows)
        => string.Join("\n", rows.Select(r => $"{r.Number}\t{r.Id}\t{r.Pod}")) + "\n";

    // ------------------------------------------------- victim selection (THE defect)

    [Fact]
    public void the_victim_is_the_lowest_numbered_non_leader()
    {
        var nodes = PostgresChaos.ParseNodes(Nodes((1, LeaderId, "pod-a"), (2, PeerId, "pod-b"), (3, OtherId, "pod-c")));

        Assert.True(PostgresChaos.TryChooseVictim(nodes, LeaderId, out var victim, out _));
        Assert.Equal(PeerId, victim!.Id);
        Assert.Equal("pod-b", victim.Description);
    }

    [Fact]
    public void the_leader_is_never_the_victim()
    {
        // A victim that is also the leader confounds the arms: the election, not the guard, would
        // explain whatever happened next.
        var nodes = PostgresChaos.ParseNodes(Nodes((1, LeaderId, "pod-a"), (2, PeerId, "pod-b")));

        Assert.True(PostgresChaos.TryChooseVictim(nodes, LeaderId, out var victim, out _));
        Assert.NotEqual(LeaderId, victim!.Id);
    }

    [Fact]
    public void an_empty_node_registry_is_refused_rather_than_yielding_an_empty_victim()
    {
        // THE defect. The shell query returned "" here, the policy became `id <> ''` — which hides
        // nothing — and the arm ran its full 240 s injecting no fault at all. Injections came back
        // 0, and 0 injections reads exactly like "the guard prevented it".
        Assert.False(PostgresChaos.TryChooseVictim([], LeaderId, out var victim, out var problem));

        Assert.Null(victim);
        Assert.Contains("node registry is empty", problem);
    }

    [Fact]
    public void a_cluster_with_no_leader_is_refused_as_unsettled()
    {
        // Mid-election: whoever is chosen now may be leader by the time the fault bites.
        var nodes = PostgresChaos.ParseNodes(Nodes((1, LeaderId, "pod-a"), (2, PeerId, "pod-b")));

        Assert.False(PostgresChaos.TryChooseVictim(nodes, "", out _, out var problem));
        Assert.Contains("no leader assignment", problem);
    }

    [Fact]
    public void a_single_node_cluster_has_no_valid_victim()
    {
        var nodes = PostgresChaos.ParseNodes(Nodes((1, LeaderId, "pod-a")));

        Assert.False(PostgresChaos.TryChooseVictim(nodes, LeaderId, out _, out var problem));
        Assert.Contains("only registered node is the leader", problem);
    }

    [Fact]
    public void a_malformed_node_row_is_dropped_rather_than_becoming_a_victim()
    {
        // A NOTICE line or a truncated row must never end up as the id interpolated into the policy.
        var nodes = PostgresChaos.ParseNodes("NOTICE: something\n\nnot-a-number\tx\ty\n2\t" + PeerId + "\tpod-b\n");

        Assert.Single(nodes);
        Assert.Equal(PeerId, nodes[0].Id);
    }

    // --------------------------------------------------------------- arm state

    [Fact]
    public void the_policy_being_present_means_armed()
        => Assert.Equal(PostgresChaos.ArmState.Armed, PostgresChaos.ParseStatus("rls-on\tpolicy-present\n"));

    [Fact]
    public void a_half_disarmed_table_still_reads_as_armed()
    {
        // Erring toward "armed" is deliberate: a false alarm costs one disarm, a missed one costs
        // every measurement taken afterwards.
        Assert.Equal(PostgresChaos.ArmState.Armed, PostgresChaos.ParseStatus("rls-on\tpolicy-absent\n"));
        Assert.Equal(PostgresChaos.ArmState.Armed, PostgresChaos.ParseStatus("rls-off\tpolicy-present\n"));
    }

    [Fact]
    public void a_fully_clean_table_is_disarmed()
        => Assert.Equal(PostgresChaos.ArmState.Disarmed, PostgresChaos.ParseStatus("rls-off\tpolicy-absent\n"));

    [Fact]
    public void no_such_table_is_disarmed_not_unknown()
    {
        // The schema has not been created yet. Nothing is armed, and refusing here would block a
        // first run on a fresh cluster for no reason.
        Assert.Equal(PostgresChaos.ArmState.Disarmed, PostgresChaos.ParseStatus(""));
    }

    [Fact]
    public void an_unreadable_status_row_is_unknown_rather_than_disarmed()
        => Assert.Equal(PostgresChaos.ArmState.Unknown, PostgresChaos.ParseStatus("ERROR:  permission denied\n"));

    [Fact]
    public void the_arm_sql_names_the_victim_and_the_disarm_drops_the_policy()
    {
        Assert.Contains($"id <> '{PeerId}'", PostgresChaos.ArmSql(PeerId));
        Assert.Contains($"drop policy if exists {PostgresChaos.SelectPolicy}", PostgresChaos.DisarmSql());
        // Only the node row is ever hidden: hiding assignments made the leader reassign the
        // victim's agents on ordinary ticks, which is churn but not the guard's domain.
        Assert.DoesNotContain("wolverine_node_assignments", PostgresChaos.ArmSql(PeerId));
    }

    // ------------------------------------------------------- config verification

    private static string ConfigLine(string setting, string? value)
    {
        // Plain concatenation: a raw interpolated literal cannot carry the trailing `}}` of this
        // JSON without escalating the `$` count, and the readability is not worth the puzzle.
        var message = value is null
            ? "CONFIG " + setting + " not available on this Wolverine build"
            : "CONFIG " + setting + "=" + value;
        var reported = value is null ? "null" : "\"" + value + "\"";

        return "{\"Message\":\"" + message + "\",\"State\":{\"Setting\":\"" + setting +
               "\",\"Value\":" + reported + "}}";
    }

    [Fact]
    public void config_is_read_from_the_pods_own_structured_output()
    {
        var reported = PodConfig.Parse([ConfigLine("StaleNodeTimeout", "00:00:04"), "not json", ""]);

        Assert.Equal("00:00:04", reported["StaleNodeTimeout"]);
    }

    [Fact]
    public void seconds_and_a_timespan_are_the_same_configuration()
    {
        // The env var says 4; Wolverine reports 00:00:04. A string compare would reject a
        // correctly configured pod and stop every run.
        var reported = PodConfig.Parse([ConfigLine("StaleNodeTimeout", "00:00:04")]);

        Assert.Empty(PodConfig.Verify(reported, new Dictionary<string, string> { ["StaleNodeTimeout"] = "4" }));
    }

    [Fact]
    public void a_knob_the_build_does_not_have_is_its_own_failure()
    {
        // "not available on this Wolverine build" and "set to something else" both start with the
        // word CONFIG, so grepping the prose could not tell them apart — and running the mutant arm
        // against a build with no such knob is measuring the control twice.
        var reported = PodConfig.Parse([ConfigLine("StaleNodeTimeout", null)]);

        var problems = PodConfig.Verify(reported, new Dictionary<string, string> { ["StaleNodeTimeout"] = "4" });

        Assert.Single(problems);
        Assert.Contains("does not have that knob", problems[0]);
    }

    [Fact]
    public void a_setting_the_pod_never_reported_is_a_failure_not_a_pass()
    {
        var problems = PodConfig.Verify(
            PodConfig.Parse([ConfigLine("SIM_JSON_LOGS", "true")]),
            new Dictionary<string, string> { ["StaleNodeTimeout"] = "4" });

        Assert.Single(problems);
        Assert.Contains("never reported it at all", problems[0]);
    }

    [Fact]
    public void the_wrong_value_is_reported_with_both_sides()
    {
        // The mislabelled-arm incident: a run reported as "stock default" actually had the gate on.
        var problems = PodConfig.Verify(
            PodConfig.Parse([ConfigLine("StaleNodeTimeout", "00:00:10")]),
            new Dictionary<string, string> { ["StaleNodeTimeout"] = "4" });

        Assert.Single(problems);
        Assert.Contains("expected '4'", problems[0]);
        Assert.Contains("pod reports '00:00:10'", problems[0]);
    }

    [Fact]
    public void the_plain_text_config_form_is_read_too()
    {
        // ChurnSim's startup emitter runs before a logger exists, so without SIM_JSON_LOGS=true
        // these lines are bare text. A JSON-only parser read a correctly configured cluster as
        // "reported no configuration at all" — found against the live RavenDB arm, which does not
        // set SIM_JSON_LOGS.
        var reported = PodConfig.Parse(["CONFIG StaleNodeTimeout=00:00:04"]);

        Assert.Equal("00:00:04", reported["StaleNodeTimeout"]);
    }

    [Fact]
    public void the_plain_text_unavailable_form_is_read_too()
    {
        var reported = PodConfig.Parse(["CONFIG StaleNodeTimeout not available on this Wolverine build"]);

        Assert.True(reported.ContainsKey("StaleNodeTimeout"));
        Assert.Null(reported["StaleNodeTimeout"]);
    }

    [Fact]
    public void a_free_text_config_banner_does_not_invent_a_setting()
    {
        // `CONFIG backend=ravendb urls=… database=churnsim` is a banner, not a knob. Split on the
        // first `=` alone it registered a setting called "backend" whose value was the rest of the
        // sentence, which then failed a perfectly reasonable expectation with a confusing message.
        var reported = PodConfig.Parse(
        [
            "CONFIG backend=ravendb urls=http://ravendb:8080 database=churnsim",
            "CONFIG ravendb database 'churnsim' ready after 1 attempt(s), created=false",
            "CONFIG json console logging enabled"
        ]);

        Assert.Empty(reported);
    }

    [Fact]
    public void a_pod_that_reported_nothing_verifies_nothing()
    {
        // An empty settings map must not pass a non-empty expectation. ChaosVerbs treats zero
        // CONFIG lines as its own refusal for the same reason.
        Assert.NotEmpty(PodConfig.Verify(
            PodConfig.Parse([]), new Dictionary<string, string> { ["StaleNodeTimeout"] = "4" }));
    }
}
