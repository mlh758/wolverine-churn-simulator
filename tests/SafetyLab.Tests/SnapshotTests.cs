using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The running-vs-assigned measurement, which until now lived in shell copies of a
/// <c>snapshot()</c> function plus a Python relay, with the answer passed between them as text.
///
/// Same discipline as <see cref="RegressionTests"/>: one test per defect that actually happened,
/// plus the golden assertions that keep a refactor from quietly changing what is being measured.
/// The log lines here are copied from runs/ captures; the deployment documents are built from
/// k8s/churnsim*.yaml, which is the spec kubectl serves back.
/// </summary>
public class SnapshotTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    // A real line, from runs/heal-test-fixes-build/iter1.
    private const string JsonStart =
        """{"Timestamp":"2026-09-10T01:49:28.1004806+00:00","EventId":0,"LogLevel":"Information","Category":"ChurnSim.SimAgentFamily","Message":"AGENT-START sim://agent288/ at 2026-09-10T01:49:28.1003011+00:00","State":{"AgentUri":"sim://agent288/","Timestamp":"09/10/2026 01:49:28 +00:00","{OriginalFormat}":"AGENT-START {AgentUri} at {Timestamp:O}"}}""";

    private const string JsonStop =
        """{"Timestamp":"2026-09-10T01:52:11.4210007+00:00","EventId":0,"LogLevel":"Information","Category":"ChurnSim.SimAgentFamily","Message":"AGENT-STOP sim://agent288/ at 2026-09-10T01:52:11.4209112+00:00","State":{"AgentUri":"sim://agent288/","Timestamp":"09/10/2026 01:52:11 +00:00","{OriginalFormat}":"AGENT-STOP {AgentUri} at {Timestamp:O}"}}""";

    // ------------------------------------------------------- the residency replay

    [Fact]
    public void an_agent_started_and_not_stopped_is_running()
    {
        var residency = AgentResidency.Replay("pod-a", [JsonStart]);

        Assert.Equal(["sim://agent288/"], residency.Running);
        Assert.Equal(1, residency.Events);
    }

    [Fact]
    public void an_agent_stopped_after_starting_is_not_running()
    {
        var residency = AgentResidency.Replay("pod-a", [JsonStart, JsonStop]);

        Assert.Empty(residency.Running);
        Assert.Equal(2, residency.Events);
        Assert.Equal(1, residency.Starts);
        Assert.Equal(1, residency.Stops);
    }

    [Fact]
    public void the_pre_json_text_format_still_replays()
    {
        // Captures taken before SIM_JSON_LOGS existed are still in runs/, and comparing an old run
        // against a new one is the whole point of keeping them.
        var residency = AgentResidency.Replay("pod-a",
        [
            "info: ChurnSim.SimAgentFamily[0]",
            "      AGENT-START sim://agent7/ at 2026-09-08T10:00:00.0000000+00:00"
        ]);

        Assert.Equal(["sim://agent7/"], residency.Running);
    }

    [Fact]
    public void a_kubectl_timestamps_prefix_does_not_glue_punctuation_onto_the_uri()
    {
        // The shell-era replay checked `line.startswith("{")` to choose the structured path. With
        // `kubectl logs --timestamps` the line starts with the timestamp instead, so it fell
        // through to the text regex, ran `AGENT-START (\S+)` against JSON, and captured
        // `sim://agent288/` with a closing quote and comma attached — an agent uri that matches
        // nothing in the assignment table, i.e. a phantom orphan. snapshot does not pass
        // --timestamps; this asserts the next caller that does cannot be bitten.
        var residency = AgentResidency.Replay("pod-a", [$"2026-09-10T01:49:28.100480612Z {JsonStart}"]);

        Assert.Equal(["sim://agent288/"], residency.Running);
    }

    [Fact]
    public void a_truncated_final_line_is_counted_but_is_not_an_event()
    {
        // Ordinary at the tail of a log being written while it is read.
        var residency = AgentResidency.Replay("pod-a", [JsonStart, """{"Timestamp":"2026-09-10T01:5"""]);

        Assert.Equal(["sim://agent288/"], residency.Running);
        Assert.Equal(1, residency.Events);
        Assert.Equal(2, residency.Lines);
    }

    // ---------------------------------------------------------- the classification

    [Fact]
    public void one_agent_on_two_pods_is_a_duplicate()
    {
        var result = Divergence.Compare(
            [("pod-a", "sim://agent1/"), ("pod-b", "sim://agent1/")],
            [("sim://agent1/", "pod-a")]);

        Assert.Equal(Divergence.Duplicate, result.Verdict);
        Assert.Equal(1, result.DuplicatedCount);
        Assert.Equal(["pod-a", "pod-b"], result.Duplicated[0].RunningOn);
        Assert.Equal(0, result.OrphanedCount);
    }

    [Fact]
    public void running_somewhere_the_table_does_not_say_is_an_orphan()
    {
        var result = Divergence.Compare(
            [("pod-b", "sim://agent1/")],
            [("sim://agent1/", "pod-a")]);

        Assert.Equal(Divergence.Diverged, result.Verdict);
        Assert.Equal(1, result.OrphanedCount);
        Assert.Equal("pod-a", result.Orphaned[0].TableSays);
    }

    [Fact]
    public void assigned_but_running_nowhere_is_missing()
    {
        var result = Divergence.Compare([], [("sim://agent1/", "pod-a")]);

        Assert.Equal(Divergence.Diverged, result.Verdict);
        Assert.Equal(1, result.MissingCount);
    }

    [Fact]
    public void a_duplicate_outranks_an_orphan_in_the_verdict()
    {
        // results.tsv has one result column, and DUPLICATE is the user-visible bug.
        var result = Divergence.Compare(
            [("pod-a", "sim://agent1/"), ("pod-b", "sim://agent1/"), ("pod-b", "sim://agent2/")],
            [("sim://agent1/", "pod-a"), ("sim://agent2/", "pod-a")]);

        Assert.Equal(Divergence.Duplicate, result.Verdict);
        Assert.Equal(1, result.DuplicatedCount);
        Assert.Equal(1, result.OrphanedCount);
    }

    [Fact]
    public void the_summary_line_is_still_the_one_every_report_txt_carries()
    {
        // runs/ and evidence/ are full of these lines; a reader comparing an old run against a
        // new one should not have to notice a reformat, and `grep -q "duplicated=0 orphaned=0
        // missing=0"` still works.
        var result = Divergence.Compare(
            [("pod-a", "sim://agent1/")],
            [("sim://agent1/", "pod-a")]);

        Assert.Equal("running=1 assigned=1 duplicated=0 orphaned=0 missing=0", result.SummaryLine);
    }

    // ------------------------------------------------------------- THE defect

    [Fact]
    public void an_empty_comparison_is_clean_which_is_why_the_sentinel_exists()
    {
        // Nothing running, nothing assigned: duplicated=0 orphaned=0 missing=0. Perfectly correct
        // as a comparison, and a catastrophe as a measurement — it is what `duplicate-rate.sh`
        // recorded as `clean` whenever the store query failed (backend.sh sent psql's stderr to
        // /dev/null) or the differ threw. The classification cannot defend against this. Only a
        // sentinel upstream can, so this test pins the reason the next one exists.
        Assert.Equal(Divergence.Clean, Divergence.Compare([], []).Verdict);
    }

    [Fact]
    public void a_snapshot_where_no_pod_logged_an_agent_event_is_refused()
    {
        // Live pods, logs that read fine, and not one AGENT-START between them. The measurement
        // cannot be taken; it must not be reported as clean.
        Assert.True(Snapshot.NothingWasObserved(
        [
            new Snapshot.PodObservation("pod-a", LogLines: 412, AgentEvents: 0, Running: 0),
            new Snapshot.PodObservation("pod-b", LogLines: 388, AgentEvents: 0, Running: 0)
        ]));
    }

    [Fact]
    public void no_pods_at_all_is_refused_rather_than_measured()
        => Assert.True(Snapshot.NothingWasObserved([]));

    [Fact]
    public void one_pod_carrying_events_is_enough_to_have_observed_something()
    {
        // A pod that placed nothing yet is a warning, not a refusal — agents assigned to it will
        // surface as `missing`, which is already loud.
        Assert.False(Snapshot.NothingWasObserved(
        [
            new Snapshot.PodObservation("pod-a", LogLines: 4000, AgentEvents: 500, Running: 250),
            new Snapshot.PodObservation("pod-b", LogLines: 12, AgentEvents: 0, Running: 0)
        ]));
    }

    // ------------------------------------------------------------ the tsv contract

    [Fact]
    public void the_tsv_row_is_the_results_file_columns_in_order()
    {
        // duplicate-rate.sh appends this verbatim. If the order here changes, every row it writes
        // afterwards is silently mislabelled — so the order is asserted, not just documented.
        var report = new Snapshot.Report(
            Backends.Postgres, DateTimeOffset.UnixEpoch, [],
            Running: 501, Assigned: 500, Duplicated: 1, Orphaned: 0, Missing: 0,
            Verdict: Divergence.Duplicate, Warnings: [],
            DuplicatedAgents: [], OrphanedAgents: [], MissingAgents: []);

        Assert.Equal("DUPLICATE\t501\t500\t1\t0\t0", Snapshot.TsvRow(report));
    }

    // ----------------------------------------------- backend detection (defect 6)

    [Fact]
    public void the_deployment_declares_which_arm_is_under_test()
        => Assert.Equal(Backends.RavenDb, StoreQueries.BackendFromDeployment(Fixture("deployment-ravendb.json")));

    [Fact]
    public void a_deployment_predating_the_ravendb_arm_is_postgres()
        => Assert.Equal(Backends.Postgres, StoreQueries.BackendFromDeployment(Fixture("deployment-legacy.json")));

    [Fact]
    public void a_downward_api_env_entry_has_no_value_and_must_not_throw()
    {
        // POD_NAME and POD_IP are valueFrom/fieldRef, so `value` is absent. A parser that assumed
        // every env entry has one would throw on every real deployment in this repo.
        var json = StoreQueries.BackendFromDeployment(Fixture("deployment-ravendb.json"));
        Assert.Equal(Backends.RavenDb, json);
    }

    // --------------------------------------------------- the query text (golden)

    [Fact]
    public void the_assigned_query_is_byte_for_byte_what_backend_sh_ran()
    {
        // Every number in RESULTS.md was taken through this exact text. Rewording it — even
        // reflowing the whitespace — would break comparability with the entire existing record for
        // no gain, so the port is pinned rather than trusted.
        const string expected =
            "select a.id || chr(9) || n.description\n" +
            "                    from wolverine.wolverine_node_assignments a\n" +
            "                    join wolverine.wolverine_nodes n on n.id = a.node_id\n" +
            "                   where a.id like 'sim://%';";

        Assert.Equal(expected, StoreQueries.AssignedSql.Replace("\r\n", "\n"));
    }

    [Fact]
    public void psql_output_parses_into_agent_and_pod()
    {
        var rows = StoreQueries.ParseTsv("sim://agent165/\tchurnsim-77dd875f59-ldtpm\nsim://agent148/\tchurnsim-77dd875f59-ldtpm\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(("sim://agent165/", "churnsim-77dd875f59-ldtpm"), rows[0]);
    }

    [Fact]
    public void a_row_that_is_not_two_columns_is_dropped_not_guessed_at()
    {
        // psql -qAt emits nothing but rows, but a NOTICE or a warning line would be one column.
        // Dropping it is what the loader this replaces did; the sentinel that catches the case where
        // EVERYTHING is dropped lives upstream, not here.
        var rows = StoreQueries.ParseTsv("NOTICE: something\nsim://agent1/\tpod-a\n");

        Assert.Single(rows);
        Assert.Equal(("sim://agent1/", "pod-a"), rows[0]);
    }
}
