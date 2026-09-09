using ChurnSim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine.Runtime.Agents;

var builder = Host.CreateApplicationBuilder(args);

// Export Wolverine's spans to Jaeger when asked. NodeAgentController wraps every assignment
// evaluation in a `wolverine_node_assignments` span and agent commands ride the ordinary message
// pipeline, so a trace shows the leader's dispatch, the receiving node's execution, and the gap
// between the two -- which is what state sampling and log scraping cannot show. Off unless
// SIM_OTLP_ENDPOINT is set, so a default run stays comparable with earlier results.
var otlp = Environment.GetEnvironmentVariable("SIM_OTLP_ENDPOINT");
if (!string.IsNullOrWhiteSpace(otlp))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "churnsim",
            serviceInstanceId: Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName))
        .WithTracing(t => t
            .AddSource("Wolverine")
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));

    Console.WriteLine($"CONFIG OTLP tracing -> {otlp}");
}

var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                       ?? "Host=localhost;Port=5433;Database=churnsim;Username=postgres;Password=postgres";

builder.UseWolverine(opts =>
{
    opts.ServiceName = "churnsim";

    // Announce who this pod is, so SafetyLab can attribute an advisory-lock holder in
    // pg_stat_activity (which knows only a client_addr) back to a Wolverine node id. Without
    // this the monitor can see that *a* lock is held and *a* node claims leadership, but not
    // whether they are the same node -- which is the whole question in a split-brain.
    // POD_NAME / POD_IP come from the downward API in k8s/churnsim.yaml.
    Console.WriteLine(
        $"SIM-IDENTITY nodeId={opts.UniqueNodeId} " +
        $"podName={Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName} " +
        $"podIp={Environment.GetEnvironmentVariable("POD_IP") ?? "unknown"} " +
        $"at {DateTimeOffset.UtcNow:O}");

    // No message handlers needed -- the point is the agent assignment plane
    opts.Discovery.DisableConventionalDiscovery();

    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    // Tighten the control-plane timers a bit so a short simulated rollout
    // exercises several health-check / assignment cycles. These stay well
    // within realistic production shapes (defaults are 10s / 30s).
    opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(2);
    opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(5);

    // The GH-3987/GH-3959 proposal settings only exist on the patched build
    // (6.33.0-proposal.*). Set them reflectively from env vars so this same
    // program runs unchanged against released 5.39, stock main, and the
    // proposal build -- on builds without the property the knob is just
    // reported as unavailable.
    void trySet(string property, object value)
    {
        var prop = opts.Durability.GetType().GetProperty(property);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(opts.Durability, value);
            Console.WriteLine($"CONFIG {property}={value}");
        }
        else
        {
            Console.WriteLine($"CONFIG {property} not available on this Wolverine build");
        }
    }

    if (int.TryParse(Environment.GetEnvironmentVariable("SIM_BATCH_SIZE"), out var batch) && batch > 0)
    {
        trySet("AgentStartBatchSize", batch);
    }

    // The "hold the rebalance until the roster stops moving" idea exists under two names: the
    // proposal called it AssignmentStabilityWindow, and GH-4367 landed it upstream as
    // AssignmentSettlePeriod. One env var drives whichever the build under test actually has, so
    // the same knob compares a proposal build against upstream main.
    //
    // Note both default to zero -- upstream's gate is OFF out of the box, so a run without this
    // variable is measuring stock behaviour, not GH-4367's.
    if (int.TryParse(Environment.GetEnvironmentVariable("SIM_STABILITY_WINDOW_SECONDS"), out var window) && window > 0)
    {
        trySet("AssignmentStabilityWindow", TimeSpan.FromSeconds(window));
        trySet("AssignmentSettlePeriod", TimeSpan.FromSeconds(window));
    }

    if (Environment.GetEnvironmentVariable("SIM_CAPACITY_AWARE") == "true")
    {
        trySet("CapacityAwareAssignment", true);

        if (double.TryParse(Environment.GetEnvironmentVariable("SIM_OVERLOAD_THRESHOLD"), out var threshold) && threshold > 0)
        {
            trySet("NodeOverloadThreshold", threshold);
        }
    }

    opts.Services.AddSingleton<IAgentFamily, SimAgentFamily>();
});

await builder.Build().RunAsync();
