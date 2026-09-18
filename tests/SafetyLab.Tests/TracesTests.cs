using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The Jaeger span summary, which until now was two inline `python3 -c '…'` blocks in
/// scripts/traces.sh — the last of the shape docs/harness-traps.md warns about.
///
/// The percentile is the interesting part to pin: it is carried over exactly from the shell
/// version, because a p95 read today has to be the same statistic as one read before the port or
/// the numbers in RESULTS.md stop being comparable to new ones.
/// </summary>
public class TracesTests
{
    private static string Payload(params (double Micros, string Op, string Trace)[] spans)
    {
        var byTrace = spans.GroupBy(x => x.Trace).Select(g =>
            $$"""{"traceID":"{{g.Key}}","spans":[""" +
            string.Join(",", g.Select(s => $$"""{"duration":{{s.Micros}},"operationName":"{{s.Op}}"}""")) +
            "]}");

        return $$"""{"data":[{{string.Join(",", byTrace)}}]}""";
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void durations_come_back_as_milliseconds()
    {
        // Jaeger reports microseconds on the wire; every number this rig prints is in ms.
        Assert.True(Traces.TrySummarise(Payload((1500, "op", "t1")), out var summary, out _));

        Assert.Single(summary.Spans);
        Assert.Equal(1.5, summary.Spans[0].Milliseconds);
        Assert.Equal("t1", summary.Spans[0].TraceId);
    }

    [Fact]
    public void every_span_of_every_trace_is_counted()
    {
        Assert.True(Traces.TrySummarise(
            Payload((1000, "a", "t1"), (2000, "b", "t1"), (3000, "c", "t2")), out var summary, out _));

        Assert.Equal(3, summary.Spans.Count);
    }

    [Fact]
    public void a_payload_that_is_not_json_is_a_problem_not_an_empty_summary()
    {
        // wget returning an HTML error page used to reach `json.load` and raise, which the shell
        // caught and printed as "(no traces returned)" — the same text an idle system produces.
        Assert.False(Traces.TrySummarise("<html>502 Bad Gateway</html>", out _, out var problem));
        Assert.Contains("did not return JSON", problem);
    }

    [Fact]
    public void a_payload_with_no_data_array_is_a_problem()
    {
        Assert.False(Traces.TrySummarise("""{"errors":[{"msg":"service not found"}]}""", out _, out var problem));
        Assert.Contains("no 'data' array", problem);
    }

    [Fact]
    public void a_trace_carrying_no_spans_is_skipped_rather_than_throwing()
    {
        Assert.True(Traces.TrySummarise("""{"data":[{"traceID":"t1"}]}""", out var summary, out _));
        Assert.Empty(summary.Spans);
    }

    // --------------------------------------------------------------- statistics

    [Fact]
    public void the_percentile_is_the_shell_versions_nearest_rank()
    {
        // ds[min(len-1, int(len*p))] over 1..10 ms: p50 -> index 5 -> 6 ms, p95 -> index 9 -> 10 ms.
        var spans = Enumerable.Range(1, 10)
            .Select(i => (Micros: i * 1000.0, Op: "op", Trace: $"t{i}")).ToArray();

        Assert.True(Traces.TrySummarise(Payload(spans), out var summary, out _));

        Assert.Equal(6.0, summary.Percentile(0.5));
        Assert.Equal(10.0, summary.Percentile(0.95));
        Assert.Equal(10.0, summary.Max);
    }

    [Fact]
    public void a_percentile_index_past_the_end_is_clamped_rather_than_throwing()
    {
        Assert.True(Traces.TrySummarise(Payload((1000, "op", "t1")), out var summary, out _));

        Assert.Equal(1.0, summary.Percentile(0.95));
        Assert.Equal(1.0, summary.Percentile(1.0));
    }

    [Fact]
    public void an_empty_summary_reports_zero_rather_than_throwing()
    {
        Assert.True(Traces.TrySummarise("""{"data":[]}""", out var summary, out _));

        Assert.Equal(0, summary.Percentile(0.5));
        Assert.Equal(0, summary.Max);
        Assert.Empty(summary.Slowest(15));
    }

    [Fact]
    public void slowest_is_slowest_first_and_deterministic()
    {
        Assert.True(Traces.TrySummarise(
            Payload((1000, "a", "t1"), (9000, "b", "t2"), (5000, "c", "t3")), out var summary, out _));

        Assert.Equal([9.0, 5.0], summary.Slowest(2).Select(x => x.Milliseconds));
    }

    // --------------------------------------------------------------- operations

    [Fact]
    public void operations_accept_both_shapes_jaeger_has_returned()
    {
        Assert.Equal(["alpha", "beta"], Traces.Operations("""{"data":["beta","alpha"]}"""));
        Assert.Equal(["alpha", "beta"],
            Traces.Operations("""{"data":[{"name":"beta"},{"name":"alpha"}]}"""));
    }

    [Fact]
    public void an_unreadable_operations_payload_is_an_empty_list_not_a_crash()
        => Assert.Empty(Traces.Operations("not json"));
}
