using SafetyLab;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The CLI is a contract: shell scripts and k8s manifests invoke it by exact spelling, and a
/// silent change to it breaks a measurement rather than a build. These tests parse without
/// invoking, so they assert the contract in microseconds and never touch a cluster.
///
/// Two halves, and the second is the reason the parser was replaced at all:
/// every invocation this repo actually issues must PARSE, and every mistake must FAIL. The
/// hand-rolled parser could only do the first — an unrecognised flag matched nothing, the option
/// fell back to its default, and the verb ran happily against a configuration nobody asked for.
/// </summary>
public class CommandLineTests
{
    private static string[] Errors(params string[] argv)
        => CommandLineDefinition.Build().Parse(argv).Errors.Select(e => e.Message).ToArray();

    private static void AssertParses(params string[] argv)
    {
        var errors = Errors(argv);
        Assert.True(errors.Length == 0,
            $"'{string.Join(' ', argv)}' should parse but reported: {string.Join("; ", errors)}");
    }

    private static void AssertRejected(params string[] argv)
    {
        var errors = Errors(argv);
        Assert.True(errors.Length > 0, $"'{string.Join(' ', argv)}' should have been REJECTED but parsed cleanly");
    }

    // -------------------------------------------- the invocations the repo actually issues

    [Theory]
    // k8s/safetylab-ravendb.yaml, verbatim.
    [InlineData("monitor", "--backend", "ravendb", "--tick-ms", "1000", "--service", "churnsim")]
    // k8s/safetylab.yaml, verbatim.
    [InlineData("monitor", "--tick-ms", "1000", "--schema", "wolverine")]
    [InlineData("monitor")]
    [InlineData("monitor", "--backend", "postgres", "--lock-id", "832201495")]
    [InlineData("monitor", "--backend", "ravendb", "--page-size", "4000", "--url", "http://ravendb:8080")]
    // The replicated arm: one url per member, comma-separated, exactly as k8s/safetylab-ravendb-cluster.yaml
    // and split-brain.sh spell them.
    [InlineData("monitor", "--backend", "ravendb", "--url", "http://ravendb-0.ravendb:8080,http://ravendb-1.ravendb:8080")]
    [InlineData("partition", "arm", "--isolate", "ravendb-2,churnsim-2", "--from", "ravendb-0,ravendb-1,churnsim-0,churnsim-1")]
    [InlineData("partition", "heal")]
    [InlineData("partition", "status")]
    [InlineData("raven-cluster", "form", "--replication-factor", "3")]
    [InlineData("raven-cluster", "status", "--url", "http://ravendb-0.ravendb:8080,http://ravendb-1.ravendb:8080")]
    [InlineData("raven-cluster", "preferred")]
    [InlineData("query", "conflicts", "--url", "http://ravendb-0.ravendb:8080", "--since", "2026-09-18T12:00:00Z")]
    // scripts/monitor.sh
    [InlineData("harvest", "--pod", "churnsim-abc")]
    [InlineData("check", "runs/rollout-1")]
    [InlineData("check", "runs/rollout-1", "--json")]
    [InlineData("check", "runs/rollout-1", "--grace", "15", "--cross-pod-grace", "2", "--converge", "60")]
    // scripts/backend.sh
    [InlineData("query", "assigned")]
    [InlineData("query", "placed")]
    [InlineData("query", "nodes")]
    [InlineData("query", "per-node")]
    [InlineData("query", "records")]
    [InlineData("query", "per-minute", "--event", "AssignmentChanged")]
    [InlineData("query", "per-minute", "--event", "NodeStopped")]
    // scripts/leader-kill.sh
    [InlineData("query", "leader")]
    [InlineData("query", "lock")]
    [InlineData("admin", "reset-metrics")]
    [InlineData("admin", "drop-database")]
    [InlineData("pods", "--label", "app=churnsim")]
    [InlineData("pick-pod", "--label", "app=safetylab")]
    [InlineData("host-pid", "--pod", "churnsim-abc", "--container", "churnsim", "--expect", "ChurnSim")]
    // scripts/duplicate-rate.sh, scripts/synth-guard-run.sh
    [InlineData("snapshot", "runs/duplicate-rate/iter1/post")]
    [InlineData("snapshot", "runs/duplicate-rate/iter1/post", "--tsv")]
    [InlineData("snapshot", "runs/foo", "--label", "app=churnsim", "--tsv")]
    // scripts/heal-test.sh
    [InlineData("overlaps", "runs/heal-test/iter1")]
    [InlineData("overlaps", "runs/heal-test/iter1", "--tsv")]
    [InlineData("overlaps", "runs/heal-test/iter1", "--grace", "2")]
    // scripts/synth-guard-run.sh
    [InlineData("chaos", "arm")]
    [InlineData("chaos", "disarm")]
    [InlineData("chaos", "status")]
    [InlineData("verify-config", "--label", "app=churnsim", "--expect", "StaleNodeTimeout=4")]
    [InlineData("count", "runs/synth-guard/x/during", "--message", "was missing from its own node snapshot")]
    // the three scripts' settle loop
    [InlineData("settle")]
    [InlineData("settle", "--expect", "500")]
    [InlineData("settle", "--series", "runs/x/placement.tsv")]
    [InlineData("settle", "--timeout-seconds", "1200", "--poll-seconds", "10", "--stable-polls", "3")]
    // justfile: traces
    [InlineData("traces")]
    [InlineData("traces", "--minutes", "90")]
    [InlineData("traces", "--minutes", "30", "--raw")]
    [InlineData("traces", "--operation", "wolverine_node_assignments")]
    public void every_invocation_the_repo_issues_still_parses(params string[] argv) => AssertParses(argv);

    // ------------------------------------------------------ and every mistake is now caught

    [Fact]
    public void a_misspelled_flag_is_rejected_instead_of_silently_ignored()
    {
        // THE reason for the rewrite. The old parser matched nothing, returned null, and the verb
        // read the DEFAULT database — measuring a store nobody asked about, with no indication.
        AssertRejected("query", "assigned", "--databse", "churnsim2");
    }

    [Fact]
    public void a_misspelled_measurement_kind_is_rejected_with_the_valid_ones_available()
    {
        AssertRejected("query", "assigend");
    }

    [Fact]
    public void an_unknown_backend_is_rejected_by_the_parser()
    {
        var errors = Errors("monitor", "--backend", "mysql");

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("postgres") && e.Contains("ravendb"));
    }

    [Fact]
    public void a_non_numeric_tick_is_rejected_rather_than_falling_back_to_the_default()
    {
        // Previously int.TryParse failed and the monitor sampled at 200ms while the operator
        // believed it was sampling at whatever they typed. Every coverage number downstream of
        // that would have been computed against the wrong declared tick.
        AssertRejected("monitor", "--tick-ms", "banana");
    }

    [Fact]
    public void a_missing_required_option_is_rejected()
    {
        AssertRejected("harvest");
        AssertRejected("host-pid");
    }

    [Fact]
    public void check_requires_its_run_directory()
    {
        AssertRejected("check");
    }

    [Fact]
    public void an_unknown_verb_is_rejected()
    {
        AssertRejected("moniter", "--tick-ms", "1000");
    }

    [Fact]
    public void a_stray_positional_argument_is_rejected()
    {
        // `safetylab query assigned extra` used to run `assigned` and drop `extra` on the floor.
        AssertRejected("query", "assigned", "extra");
    }

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void defaults_match_what_the_scripts_assume()
    {
        var parse = CommandLineDefinition.Build().Parse("monitor");
        var options = parse.CommandResult.Command.Options;

        // These defaults are load-bearing: k8s/safetylab.yaml relies on --schema defaulting to
        // wolverine, and backend.sh relies on --backend defaulting to postgres so a pre-RavenDB
        // capture still behaves.
        Assert.Contains(options, o => o.Name == "--schema");
        Assert.Contains(options, o => o.Name == "--backend");
        Assert.Empty(parse.Errors);
    }

    [Fact]
    public void help_is_generated_rather_than_hand_maintained()
    {
        // The old usage text was a heredoc kept in sync by hand, and it drifted twice in one
        // session. Asserting the verbs are discoverable pins that they come from the definitions.
        var root = CommandLineDefinition.Build();
        var verbs = root.Subcommands.Select(c => c.Name).ToArray();

        Assert.Equal(
            new[] { "admin", "chaos", "check", "count", "harvest", "host-pid", "monitor", "overlaps", "partition", "pick-pod", "pods", "query", "raven-cluster", "settle", "snapshot", "traces", "verify-config" },
            verbs.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
