using ChurnSim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Wolverine.Runtime.Agents;

var builder = Host.CreateApplicationBuilder(args);

// Which message store this image was BUILT for is a compile-time fact (see ChurnSim.csproj's
// SimBackend switch), but SIM_BACKEND on the deployment is what every measurement script reads
// to pick its query path. If the two disagree, the arm is mislabelled and the run is scrap --
// and a mislabelled arm has already cost this rig three launches (see duplicate-rate.sh). Die
// here rather than produce numbers filed under the wrong backend. Read off the configuration root
// rather than SimOptions so this is reported ahead of any binder complaint about another knob.
var declaredBackend = builder.Configuration["SIM_BACKEND"];
if (!string.IsNullOrWhiteSpace(declaredBackend) &&
    !string.Equals(declaredBackend, SimBackend.Name, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"SIM_BACKEND is '{declaredBackend}' but this image was built for '{SimBackend.Name}'. " +
        "Rebuild with ./scripts/deploy.sh --backend " + declaredBackend +
        ", or fix the deployment's SIM_BACKEND. Refusing to run a mislabelled arm.");
}

var sim = builder.Configuration.Get<SimOptions>() ?? new SimOptions();

// Startup lines are written before a logger exists, but they must not break the log stream:
// DuckDB reads the pod log as newline-delimited JSON, and a single bare-text line makes the whole
// file unparseable. Emit them in the same shape the JSON console formatter uses.
void emit(string category, string message, IDictionary<string, object?> state)
{
    if (!sim.JsonLogs)
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
if (sim.JsonLogs)
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
if (!string.IsNullOrWhiteSpace(sim.OtlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "churnsim",
            serviceInstanceId: sim.PodName))
        .WithTracing(t => t
            .AddSource("Wolverine")
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o => o.Endpoint = new Uri(sim.OtlpEndpoint)));

    emit("ChurnSim.Startup", $"CONFIG OTLP tracing -> {sim.OtlpEndpoint}",
        new Dictionary<string, object?> { ["Setting"] = "SIM_OTLP_ENDPOINT", ["Value"] = sim.OtlpEndpoint });
}

builder.UseWolverine(opts =>
{
    opts.ServiceName = "churnsim";

    // Announce who this pod is, so SafetyLab can attribute an advisory-lock holder in
    // pg_stat_activity (which knows only a client_addr) back to a Wolverine node id. Without
    // this the monitor can see that *a* lock is held and *a* node claims leadership, but not
    // whether they are the same node -- which is the whole question in a split-brain.
    // POD_NAME / POD_IP come from the downward API in k8s/churnsim.yaml.
    emit("ChurnSim.Startup",
        $"SIM-IDENTITY nodeId={opts.UniqueNodeId} podName={sim.PodName} podIp={sim.PodIp} at {DateTimeOffset.UtcNow:O}",
        new Dictionary<string, object?>
        {
            ["NodeId"] = opts.UniqueNodeId.ToString(),
            ["PodName"] = sim.PodName,
            ["PodIp"] = sim.PodIp
        });

    // No message handlers needed -- the point is the agent assignment plane
    opts.Discovery.DisableConventionalDiscovery();

    // The one line that differs between arms. Everything below -- the control-plane timers, the
    // proposal knobs, the agent family -- is held constant on purpose, so a difference between a
    // PostgreSQL run and a RavenDB run is the store's semantics and not the simulation's.
    SimBackend.Configure(opts, builder.Configuration, emit);

    // Tighten the control-plane timers a bit so a short simulated rollout
    // exercises several health-check / assignment cycles. These stay well
    // within realistic production shapes (defaults are 10s / 30s).
    opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(2);
    opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(5);

    // The GH-3987/GH-3959 proposal settings only exist on the patched build
    // (6.33.0-proposal.*). Set them reflectively so this same program runs
    // unchanged against released 5.39, stock main, and the proposal build --
    // on builds without the property the knob is just reported as unavailable.
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

    if (sim.BatchSize > 0)
    {
        trySet("AgentStartBatchSize", sim.BatchSize.Value);
    }

    // The "hold the rebalance until the roster stops moving" idea exists under two names: the
    // proposal called it AssignmentStabilityWindow, and GH-4367 landed it upstream as
    // AssignmentSettlePeriod. One env var drives whichever the build under test actually has, so
    // the same knob compares a proposal build against upstream main.
    //
    // Note both default to zero -- upstream's gate is OFF out of the box, so a run without this
    // variable is measuring stock behaviour, not GH-4367's.
    if (sim.StabilityWindowSeconds > 0)
    {
        var window = TimeSpan.FromSeconds(sim.StabilityWindowSeconds.Value);
        trySet("AssignmentStabilityWindow", window);
        trySet("AssignmentSettlePeriod", window);
    }

    // For the synthetic-self-guard runs: shrink the stale line toward the (2s) heartbeat cadence so
    // that GC pauses / start storms make peers eject live nodes' rows, opening the read-miss window
    // where a node's own snapshot omits its row and the reconcile sweep must sit the tick out.
    if (sim.StaleNodeTimeoutSeconds > 0)
    {
        trySet("StaleNodeTimeout", TimeSpan.FromSeconds(sim.StaleNodeTimeoutSeconds.Value));
    }

    // Node-side reconcile sweep threshold (consecutive ticks). Only exists on sweep builds; 0 disables.
    if (sim.ReconcileThreshold is { } reconcile)
    {
        trySet("LocalAgentReconciliationThreshold", reconcile);
    }

    if (sim.CapacityAware)
    {
        trySet("CapacityAwareAssignment", true);

        if (sim.OverloadThreshold > 0)
        {
            trySet("NodeOverloadThreshold", sim.OverloadThreshold.Value);
        }

        // CapacityAwareAssignment needs a NodeLoadMonitor; Wolverine refuses to start with it
        // on and the monitor null. MemoryPressureLoadMonitor is constructed reflectively, like
        // trySet, because the type does not exist on 6.39.0 and earlier.
        //
        // SIM_NO_LOAD_MONITOR skips this, to observe that startup refusal. On a build without
        // MemoryPressureLoadMonitor it leaves the build's built-in default in place.
        if (!sim.NoLoadMonitor)
        {
            var monitorType = opts.Durability.GetType().Assembly
                .GetType("Wolverine.Runtime.Agents.MemoryPressureLoadMonitor");

            if (monitorType == null)
            {
                emit("ChurnSim.Startup", "CONFIG NodeLoadMonitor not available on this Wolverine build",
                    new Dictionary<string, object?> { ["Setting"] = "NodeLoadMonitor", ["Value"] = null });
            }
            else
            {
                var monitor = Activator.CreateInstance(monitorType)!;
                trySet("NodeLoadMonitor", monitor);

                // Print the monitor's denominator so a run can be judged on what each node
                // actually measures. Limit has three outcomes, and they must not be conflated:
                //
                //   no Limit property  the monitor divides by the GC budget. Readings are not
                //                      comparable to cgroup-scaled ones: they differ by 0.75 for
                //                      the same resident bytes (CALIBRATION.md).
                //   Limit = bytes      the monitor divides by the cgroup memory limit.
                //   Limit = null       no memory limit was found. The monitor returns null, the
                //                      leader reads that as unlimited headroom, and the node
                //                      advertises no load for the whole run -- which voids a
                //                      capacity arm.
                var limitProp = monitorType.GetProperty("Limit");
                var limit = limitProp?.GetValue(monitor) as long?;
                var (text, value) = limitProp == null
                    ? ("LOAD-MONITOR denominator=gc-budget (pre-GH-4589 build, no Limit to read)", "gc-budget")
                    : limit is { } bytes
                        ? ($"LOAD-MONITOR denominator=cgroup limit={bytes} bytes ({bytes / 1024 / 1024} MiB)", bytes.ToString())
                        : ("LOAD-MONITOR denominator=NONE -- *** this node will advertise no load ***", "none");

                emit("ChurnSim.Startup", text,
                    new Dictionary<string, object?>
                    {
                        ["Setting"] = "NodeLoadMonitorLimit",
                        ["Value"] = value
                    });
            }
        }
    }

    opts.Services.AddSingleton(sim);
    opts.Services.AddSingleton<IAgentFamily, SimAgentFamily>();
});

await builder.Build().RunAsync();
