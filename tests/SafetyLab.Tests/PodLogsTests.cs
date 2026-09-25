using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The node log tailer's host side: decoding the kubelet's CRI line format, and the coverage
/// sentinel that refuses a capture with a live pod missing from it.
///
/// The CRI lines are copied from /data/podlogs on the minikube node, 2026-09-25.
/// </summary>
public class PodLogsTests
{
    private const string Identity =
        "2026-09-25T18:13:25.660195389Z stdout F SIM-IDENTITY nodeId=a0fd9e12-ec9f-4953-9114-d7cd4f120534 " +
        "podName=churnsim-5d9674d869-hszg6 podIp=10.244.0.135 at 2026-09-25T18:13:25.6587221+00:00";

    private const string Config = "2026-09-25T18:13:25.662277099Z stdout F CONFIG backend=mysql schema=wolverine";

    // ------------------------------------------------------------------ decoding

    [Fact]
    public void a_full_record_is_the_application_line_without_the_prefix()
    {
        var decoded = CriLog.Decode([Identity, Config]);

        Assert.Equal(2, decoded.Entries.Count);
        Assert.StartsWith("SIM-IDENTITY nodeId=a0fd9e12", decoded.Entries[0].Text);
        Assert.Equal("CONFIG backend=mysql schema=wolverine", decoded.Entries[1].Text);
        Assert.Equal("stdout", decoded.Entries[0].Stream);
        // The node writes nanoseconds; .NET keeps 100ns ticks and ROUNDS the rest, so .660195389
        // reads as .6601954 rather than .6601953.
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 18, 13, 25, TimeSpan.Zero).AddTicks(6601954),
            decoded.Entries[0].Ts);
        Assert.Equal(0, decoded.Malformed);
        Assert.Equal(0, decoded.Joined);
    }

    [Fact]
    public void partial_records_are_joined_into_one_line()
    {
        // A JSON record longer than the runtime's 16 KB buffer arrives as P, P, F. Handing any
        // fragment on as a line would give DuckDB and the harvester an unparseable object each.
        var decoded = CriLog.Decode([
            "2026-09-25T18:13:25.100000000Z stdout P {\"Message\":\"AGENT-START sim://agent1/",
            "2026-09-25T18:13:25.100000001Z stdout P  at 2026-09-25T18:13:25.0000000+00:00\",",
            "2026-09-25T18:13:25.100000002Z stdout F \"State\":{}}",
            Config
        ]);

        Assert.Equal(2, decoded.Entries.Count);
        Assert.Equal(
            "{\"Message\":\"AGENT-START sim://agent1/ at 2026-09-25T18:13:25.0000000+00:00\",\"State\":{}}",
            decoded.Entries[0].Text);
        Assert.Equal(4, decoded.Records);
        Assert.Equal(2, decoded.Joined);
        // The joined line carries the FIRST fragment's clock reading.
        Assert.Equal(100000000 / 100, decoded.Entries[0].Ts.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void partials_are_joined_per_stream_so_stderr_cannot_interleave_into_stdout()
    {
        var decoded = CriLog.Decode([
            "2026-09-25T18:13:25.100000000Z stdout P abc",
            "2026-09-25T18:13:25.100000001Z stderr F Unhandled exception",
            "2026-09-25T18:13:25.100000002Z stdout F def"
        ]);

        Assert.Equal(["Unhandled exception", "abcdef"], decoded.Entries.Select(x => x.Text));
    }

    [Fact]
    public void a_fragment_the_container_died_inside_is_kept()
    {
        // SIGKILL mid-line: the last record is a P with no F to follow. It is what the process
        // wrote, so it is emitted rather than dropped for being unterminated.
        var decoded = CriLog.Decode([Config, "2026-09-25T18:13:26.000000000Z stdout P {\"Message\":\"AGENT-ST"]);

        Assert.Equal(2, decoded.Entries.Count);
        Assert.Equal("{\"Message\":\"AGENT-ST", decoded.Entries[1].Text);
        Assert.Equal(0, decoded.Malformed);
    }

    [Fact]
    public void an_empty_application_line_survives_with_or_without_the_trailing_space()
    {
        var decoded = CriLog.Decode([
            "2026-09-25T18:13:25.100000000Z stdout F ",
            "2026-09-25T18:13:25.100000001Z stdout F",
            Config
        ]);

        Assert.Equal(["", "", "CONFIG backend=mysql schema=wolverine"], decoded.Entries.Select(x => x.Text));
    }

    [Fact]
    public void a_record_that_is_not_cri_is_counted_not_guessed_at()
    {
        // A bare application line in this file would mean the tailer stopped copying verbatim.
        // It cannot be trusted as content and it cannot be ignored silently.
        var decoded = CriLog.Decode([Config, "CONFIG backend=mysql schema=wolverine", "not-a-date stdout F x"]);

        Assert.Single(decoded.Entries);
        Assert.Equal(2, decoded.Malformed);
    }

    [Fact]
    public void blank_lines_are_not_records()
    {
        var decoded = CriLog.Decode(["", Config, ""]);

        Assert.Single(decoded.Entries);
        Assert.Equal(1, decoded.Records);
    }

    // ---------------------------------------------------------------- file names

    [Theory]
    [InlineData("churnsim-5d9674d869-hszg6.0.cri", "churnsim-5d9674d869-hszg6", 0)]
    [InlineData("churnsim-5d9674d869-hszg6.12.cri", "churnsim-5d9674d869-hszg6", 12)]
    [InlineData("churnsim-2.1.cri", "churnsim-2", 1)]
    public void the_tailers_file_name_is_pod_and_attempt(string name, string pod, int attempt)
    {
        Assert.True(CriLog.TryParseFileName(name, out var p, out var a));
        Assert.Equal(pod, p);
        Assert.Equal(attempt, a);
    }

    [Theory]
    [InlineData(".tail.db")]
    [InlineData(".tail.db-wal")]
    [InlineData("churnsim-5d9674d869-hszg6.cri")]
    [InlineData("churnsim-5d9674d869-hszg6.0.jsonl")]
    [InlineData("")]
    public void the_tailers_own_files_are_not_logs(string name)
        => Assert.False(CriLog.TryParseFileName(name, out _, out _));

    [Fact]
    public void the_listing_keeps_only_logs_in_pod_then_attempt_order()
    {
        var files = PodLogs.Files([".tail.db", "b.1.cri", "a.0.cri", "", "b.0.cri", ".tail.db-shm"]);

        Assert.Equal(["a.0.cri", "b.0.cri", "b.1.cri"], files.Select(x => x.Name));
    }

    // ------------------------------------------------------------------ coverage

    private static PodInfo Live(string name, int restarts)
        => new(name, "Running", Ready: true, Terminating: false, RestartCount: restarts);

    [Fact]
    public void a_live_pod_with_a_file_for_its_current_attempt_is_covered()
    {
        var files = PodLogs.Files(["pod-a.0.cri", "pod-b.0.cri", "pod-b.1.cri"]);

        Assert.Empty(PodLogs.Uncovered([Live("pod-a", 0), Live("pod-b", 1)], files));
    }

    [Fact]
    public void a_live_pod_with_no_file_is_not_covered()
    {
        var files = PodLogs.Files(["pod-a.0.cri"]);

        Assert.Equal(["pod-b (attempt 0)"], PodLogs.Uncovered([Live("pod-a", 0), Live("pod-b", 0)], files));
    }

    [Fact]
    public void a_file_for_the_previous_attempt_does_not_cover_the_restarted_container()
    {
        // The victim's log is there; the survivor's is not. Reporting this as covered would put
        // the pre-kill lines under the post-kill node's name and nothing under the live one.
        var files = PodLogs.Files(["pod-a.0.cri"]);

        Assert.Equal(["pod-a (attempt 1)"], PodLogs.Uncovered([Live("pod-a", 1)], files));
    }

    [Fact]
    public void a_file_is_described_by_what_the_cluster_says_about_its_pod()
    {
        var pods = new List<PodInfo>
        {
            Live("pod-a", 1),
            new("pod-t", "Running", Ready: true, Terminating: true, RestartCount: 0)
        };

        Assert.Equal("live", PodLogs.Describe(new PodLogs.NodeFile("pod-a", 1, "pod-a.1.cri"), pods));
        Assert.Equal("previous", PodLogs.Describe(new PodLogs.NodeFile("pod-a", 0, "pod-a.0.cri"), pods));
        Assert.Equal("terminating", PodLogs.Describe(new PodLogs.NodeFile("pod-t", 0, "pod-t.0.cri"), pods));
        Assert.Equal("gone", PodLogs.Describe(new PodLogs.NodeFile("pod-z", 0, "pod-z.0.cri"), pods));
    }

    // ---------------------------------------------------------------- the run

    [Theory]
    [InlineData("rollout-1")]
    [InlineData("leader-kill-mysql-20260925T210000Z")]
    [InlineData("run_2.a")]
    [InlineData("x")]
    public void a_run_name_is_a_plain_directory_name(string name)
        => Assert.True(PodLogs.IsValidRunName(name));

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("-flag")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a;rm")]
    [InlineData("$HOME")]
    public void anything_that_is_not_a_plain_directory_name_is_refused(string name)
        => Assert.False(PodLogs.IsValidRunName(name));

    private static string TailerListing(string tailerRun, string? initRun = null)
    {
        var init = ",\"initContainers\":[{\"name\":\"mkdir\",\"env\":[{\"name\":\"LOGTAIL_RUN\",\"value\":\"" +
                   (initRun ?? tailerRun) + "\"}]}]";
        return "{\"items\":[{\"metadata\":{\"name\":\"logtail-abc\"},\"spec\":{\"containers\":[" +
               "{\"name\":\"fluent-bit\",\"env\":[{\"name\":\"LOGTAIL_RUN\",\"value\":\"" + tailerRun + "\"}]}," +
               "{\"name\":\"shelf\"}]" + init + "}}]}";
    }

    [Fact]
    public void the_run_is_read_from_the_tailer_pods_own_environment()
    {
        Assert.Equal("rollout-1", PodLogs.RunOf(TailerListing("rollout-1"), "logtail-abc", out var problem));
        Assert.Equal("", problem);
    }

    [Fact]
    public void a_tailer_with_no_run_variable_is_a_refusal_not_a_default()
    {
        const string listing = """{"items":[{"metadata":{"name":"logtail-abc"},"spec":{"containers":[{"name":"fluent-bit"}]}}]}""";

        Assert.Null(PodLogs.RunOf(listing, "logtail-abc", out var problem));
        Assert.Contains("LOGTAIL_RUN", problem);
    }

    [Fact]
    public void the_init_container_and_the_tailer_must_agree_on_the_run()
    {
        // `kubectl set env` that reached one container and not the other: the tailer would open a
        // directory nobody created, and every read after that would be of a run nobody is on.
        Assert.Null(PodLogs.RunOf(TailerListing("run-2", initRun: "run-1"), "logtail-abc", out var problem));
        Assert.Contains("run-1", problem);
        Assert.Contains("run-2", problem);
    }

    [Fact]
    public void a_pod_not_in_the_listing_is_a_refusal()
    {
        Assert.Null(PodLogs.RunOf(TailerListing("rollout-1"), "logtail-xyz", out var problem));
        Assert.Contains("logtail-xyz", problem);
    }
}
