using ChurnSim;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine.Runtime.Agents;

var builder = Host.CreateApplicationBuilder(args);

// Startup lines are written before a logger exists, but they must not break the log stream:
// DuckDB reads the pod log as newline-delimited JSON, and a single bare-text line makes the whole
// file unparseable. Emit them in the same shape the JSON console formatter uses.
var jsonLogs = Environment.GetEnvironmentVariable("SIM_JSON_LOGS") == "true";
void emit(string category, string message, IDictionary<string, object?> state)
{
    if (!jsonLogs)
    {
        Console.WriteLine(message);
        return;
    }

    var payload = new Dictionary<string, object?>
    {
        ["Timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["EventId"] = 0,
        ["LogLevel"] = "Information",
        ["Category"] = category,
        ["Message"] = message,
        ["State"] = state
    };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload));
}

// Structured stdout, so a node-level collector can ingest logs without regex and without the app
// paying for an exporter. .NET's JSON formatter emits each message-template parameter as a named
// field under "State", so `LogInformation("Successfully started agent {AgentUri} on node
// {NodeNumber}", ...)` arrives queryable instead of as prose to be pattern-matched.
//
// Deliberately NOT in-process OTLP log export: this rig hunts races and timing cadences, and an
// exporter's batching threads and allocations sit inside the process under measurement. Writing a
// differently-formatted line to stdout costs the same as writing the old one.
if (Environment.GetEnvironmentVariable("SIM_JSON_LOGS") == "true")
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "O";              // without this the formatter emits no timestamp at all
        o.IncludeScopes = false;
        o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
    });
    emit("ChurnSim.Startup", "CONFIG json console logging enabled",
        new Dictionary<string, object?> { ["Setting"] = "SIM_JSON_LOGS", ["Value"] = "true" });
}

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

    emit("ChurnSim.Startup", $"CONFIG OTLP tracing -> {otlp}",
        new Dictionary<string, object?> { ["Setting"] = "SIM_OTLP_ENDPOINT", ["Value"] = otlp });
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
    var podName = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;
    var podIp = Environment.GetEnvironmentVariable("POD_IP") ?? "unknown";
    emit("ChurnSim.Startup",
        $"SIM-IDENTITY nodeId={opts.UniqueNodeId} podName={podName} podIp={podIp} at {DateTimeOffset.UtcNow:O}",
        new Dictionary<string, object?>
        {
            ["NodeId"] = opts.UniqueNodeId.ToString(),
            ["PodName"] = podName,
            ["PodIp"] = podIp
        });

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
            emit("ChurnSim.Startup", $"CONFIG {property}={value}",
                new Dictionary<string, object?> { ["Setting"] = property, ["Value"] = value?.ToString() });
        }
        else
        {
            emit("ChurnSim.Startup", $"CONFIG {property} not available on this Wolverine build",
                new Dictionary<string, object?> { ["Setting"] = property, ["Value"] = null });
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
