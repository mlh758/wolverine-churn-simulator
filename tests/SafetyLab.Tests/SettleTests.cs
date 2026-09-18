using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The settle predicate and the deployment reading it depends on. Three shell copies of
/// `wait_settled` compared against a hardcoded 500; these pin what replaced them.
/// </summary>
public class SettleTests
{
    private static Settle.Sample S(int at, int placed) => new(at, placed);

    [Fact]
    public void the_count_must_hold_across_consecutive_polls()
    {
        // Arriving is not converging. Placement passes THROUGH the target on its way past it
        // during a rollout, so a single sample at the right number is not a settled cluster.
        Assert.False(Settle.IsSettled([S(0, 500)], 500, 3));
        Assert.False(Settle.IsSettled([S(0, 500), S(10, 500)], 500, 3));
        Assert.True(Settle.IsSettled([S(0, 500), S(10, 500), S(20, 500)], 500, 3));
    }

    [Fact]
    public void a_dip_below_the_target_restarts_the_count()
    {
        Assert.False(Settle.IsSettled([S(0, 500), S(10, 499), S(20, 500)], 500, 3));
    }

    [Fact]
    public void only_the_most_recent_polls_count()
    {
        // Earlier good runs do not bank credit toward a later one.
        Assert.True(Settle.IsSettled(
            [S(0, 500), S(10, 500), S(20, 400), S(30, 500), S(40, 500), S(50, 500)], 500, 3));
    }

    [Fact]
    public void overshooting_the_target_is_not_settled()
    {
        // More placed than configured means duplicates in the table, not convergence.
        Assert.False(Settle.IsSettled([S(0, 501), S(10, 501), S(20, 501)], 500, 3));
    }

    [Fact]
    public void an_empty_series_is_never_settled()
        => Assert.False(Settle.IsSettled([], 500, 3));

    // ------------------------------------------- where the target comes from

    // Plain concatenation: a raw interpolated literal cannot carry this many trailing braces
    // without escalating the `$` count, and the readability is not worth the puzzle.
    private static string Deployment(string env)
        => "{\"spec\":{\"template\":{\"spec\":{\"containers\":[{\"name\":\"churnsim\",\"env\":[" +
           env + "]}]}}}}";

    [Fact]
    public void the_target_comes_from_the_deployment_not_a_literal()
    {
        var json = Deployment("""{"name":"SIM_AGENT_COUNT","value":"500"}""");

        Assert.Equal("500", StoreQueries.EnvFromDeployment(json, "SIM_AGENT_COUNT"));
    }

    [Fact]
    public void a_deployment_with_no_agent_count_yields_null_not_a_guess()
    {
        // ChurnSim's own default is 20 when the var is absent, not 500 — so guessing 500 here
        // would mean polling forever against a cluster that had already converged.
        var json = Deployment("""{"name":"SIM_BACKEND","value":"postgres"}""");

        Assert.Null(StoreQueries.EnvFromDeployment(json, "SIM_AGENT_COUNT"));
    }

    [Fact]
    public void a_downward_api_variable_has_no_literal_value()
    {
        var json = Deployment("""{"name":"POD_NAME","valueFrom":{"fieldRef":{"fieldPath":"metadata.name"}}}""");

        Assert.Null(StoreQueries.EnvFromDeployment(json, "POD_NAME"));
    }

    [Fact]
    public void the_placed_query_is_byte_for_byte_what_backend_sh_ran()
        => Assert.Equal(
            "select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';",
            StoreQueries.PlacedSql.Replace("\r\n", "\n"));
}
