using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The duplicate-healing timeline: every window where one agent ran on two pods at once, and
/// whether the cluster corrected itself.
///
/// This is the measurement a convergence fix is judged on, so the classification rules are pinned
/// rather than trusted. Same discipline as the rest of this project: the shapes that bit us are
/// the shapes that get asserted.
/// </summary>
public class OverlapsTests
{
    private static DateTimeOffset T(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static (DateTimeOffset, bool, string, string) Start(int at, string agent, string pod)
        => (T(at), true, agent, pod);

    private static (DateTimeOffset, bool, string, string) Stop(int at, string agent, string pod)
        => (T(at), false, agent, pod);

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    // ------------------------------------------------------------ classification

    [Fact]
    public void a_clean_handover_with_no_overlap_is_never()
    {
        var result = Overlaps.Find(
            [Start(0, "sim://a/", "pod-a"), Stop(100, "sim://a/", "pod-a"), Start(101, "sim://a/", "pod-b")],
            Grace);

        Assert.Equal(Overlaps.Never, result.Outcome);
        Assert.Empty(result.Healed);
        Assert.Empty(result.Persisted);
    }

    [Fact]
    public void both_copies_running_then_one_stops_is_healed()
    {
        // The cluster was wrong for 30 s and then fixed itself. That duration is the finding.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(100, "sim://a/", "pod-b"),
            Stop(130, "sim://a/", "pod-a"),
            Stop(400, "sim://a/", "pod-b")
        ], Grace);

        Assert.Equal(Overlaps.Healed, result.Outcome);
        Assert.Single(result.Healed);
        Assert.Equal(30, result.Healed[0].Duration.TotalSeconds);
        Assert.Equal("30.0", result.LongestHealSeconds);
    }

    [Fact]
    public void both_copies_still_running_at_the_end_of_the_capture_is_persisted()
    {
        // Neither side ever stopped. Stock 6.35.0 did this: a duplicate still live 40 minutes on.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(100, "sim://a/", "pod-b"),
            Start(500, "sim://b/", "pod-b")   // the last event: where the capture ends
        ], Grace);

        Assert.Equal(Overlaps.Persisted, result.Outcome);
        Assert.Single(result.Persisted);
        Assert.Equal(400, result.Persisted[0].Duration.TotalSeconds);
    }

    [Fact]
    public void persisted_outranks_healed_in_the_outcome()
    {
        // One duplicate corrected and another left standing is a failure to converge, and
        // heal-test.sh's results file has one outcome column to say so in.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(10, "sim://a/", "pod-b"),
            Stop(50, "sim://a/", "pod-a"),
            Start(0, "sim://b/", "pod-a"),
            Start(10, "sim://b/", "pod-b"),
            Start(200, "sim://c/", "pod-a")
        ], Grace);

        Assert.Equal(Overlaps.Persisted, result.Outcome);
        Assert.Single(result.Healed);
        Assert.Single(result.Persisted);
    }

    [Fact]
    public void an_overlap_inside_the_grace_window_is_handover_not_duplication()
    {
        // Pod clocks differ, and a handover that briefly runs both copies while the stop is in
        // flight is the protocol working. Two seconds exactly is NOT over the line.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(100, "sim://a/", "pod-b"),
            Stop(102, "sim://a/", "pod-a"),
            Stop(300, "sim://a/", "pod-b")
        ], Grace);

        Assert.Equal(Overlaps.Never, result.Outcome);
    }

    [Fact]
    public void the_same_agent_restarting_on_one_pod_is_not_an_overlap_with_itself()
    {
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Stop(50, "sim://a/", "pod-a"),
            Start(60, "sim://a/", "pod-a"),
            Stop(200, "sim://a/", "pod-a")
        ], Grace);

        Assert.Equal(Overlaps.Never, result.Outcome);
    }

    [Fact]
    public void a_repeated_start_with_no_stop_between_keeps_the_earlier_time()
    {
        // Otherwise a re-logged start would shorten the interval and hide the front of an overlap.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(50, "sim://a/", "pod-a"),
            Start(10, "sim://a/", "pod-b"),
            Stop(300, "sim://a/", "pod-b"),
            Stop(400, "sim://a/", "pod-a")
        ], Grace);

        Assert.Equal(Overlaps.Healed, result.Outcome);
        Assert.Equal(290, result.Healed[0].Duration.TotalSeconds);
    }

    // ------------------------------------------------------------- THE defect

    [Fact]
    public void an_empty_event_stream_is_never_which_is_why_the_sentinel_exists()
    {
        // No events: agents=0, healed=0, persisted=0, outcome `never` — "no duplicate at all".
        // Correct as an analysis and catastrophic as a measurement, because heal-test.sh deletes
        // the raw logs on `never`. Overlaps.Run refuses before reaching this, on Events == 0.
        var result = Overlaps.Find([], Grace);

        Assert.Equal(Overlaps.Never, result.Outcome);
        Assert.Equal(0, result.Events);
    }

    // ---------------------------------------------------------- the tsv contract

    [Fact]
    public void the_tsv_row_is_the_results_file_columns_in_order()
    {
        // heal-test.sh appends this verbatim after the iteration number.
        var result = Overlaps.Find(
        [
            Start(0, "sim://a/", "pod-a"),
            Start(100, "sim://a/", "pod-b"),
            Stop(130, "sim://a/", "pod-a"),
            Stop(400, "sim://a/", "pod-b")
        ], Grace);

        Assert.Equal("HEALED\t1\t0\t30.0", result.TsvRow);
    }

    [Fact]
    public void nothing_healed_carries_a_dash_not_a_zero()
    {
        // A zero would read as "healed instantly"; the column has always been "-" for "no healed
        // overlap to measure".
        Assert.Equal("-", Overlaps.Find([], Grace).LongestHealSeconds);
        Assert.Equal("never\t0\t0\t-", Overlaps.Find([], Grace).TsvRow);
    }

    [Fact]
    public void the_summary_line_keeps_the_shape_every_overlaps_txt_carries()
        => Assert.Equal("agents=0 overlaps_healed=0 overlaps_persisted=0",
            Overlaps.Find([], Grace).SummaryLine);

    // ------------------------------------------------- timestamps on the event path

    [Fact]
    public void a_json_log_line_yields_its_record_timestamp()
    {
        const string line =
            """{"Timestamp":"2026-09-10T01:49:28.1004806+00:00","LogLevel":"Information","Category":"ChurnSim.SimAgentFamily","Message":"AGENT-START sim://agent288/ at 2026-09-10T01:49:28.1003011+00:00","State":{"AgentUri":"sim://agent288/"}}""";

        Assert.True(AgentResidency.TryParseEvent(line, out var e));
        Assert.True(e.Start);
        Assert.Equal("sim://agent288/", e.AgentUri);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T01:49:28.1004806+00:00"), e.Timestamp);
    }

    [Fact]
    public void a_text_log_line_yields_the_timestamp_embedded_in_the_message()
    {
        Assert.True(AgentResidency.TryParseEvent(
            "      AGENT-STOP sim://agent7/ at 2026-09-08T10:00:00.0000000+00:00", out var e));

        Assert.False(e.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T10:00:00+00:00"), e.Timestamp);
    }
}
