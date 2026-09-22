using Microsoft.Extensions.Configuration;

namespace ChurnSim;

/// <summary>
/// The SIM_* knobs, bound from configuration at startup.
///
/// The env var names are contractual: k8s/*.yaml sets them, scripts read them back off the
/// deployment (scripts/split-brain.sh refuses a cut unless `safetylab verify-config --expect`
/// matches), and RESULTS.md files runs by them. Hence ConfigurationKeyName on every property
/// rather than a name the binder derives.
/// </summary>
public sealed class SimOptions
{
    [ConfigurationKeyName("SIM_JSON_LOGS")] public bool JsonLogs { get; set; }

    [ConfigurationKeyName("SIM_OTLP_ENDPOINT")] public string? OtlpEndpoint { get; set; }

    // Wolverine DurabilitySettings knobs, applied reflectively in Program.cs; unset leaves
    // whatever the build under test defaults to.

    [ConfigurationKeyName("SIM_BATCH_SIZE")] public int? BatchSize { get; set; }

    [ConfigurationKeyName("SIM_STABILITY_WINDOW_SECONDS")] public int? StabilityWindowSeconds { get; set; }

    [ConfigurationKeyName("SIM_STALE_NODE_TIMEOUT_SECONDS")] public int? StaleNodeTimeoutSeconds { get; set; }

    /// <summary>Consecutive ticks; 0 disables the sweep.</summary>
    [ConfigurationKeyName("SIM_RECONCILE_THRESHOLD")] public int? ReconcileThreshold { get; set; }

    [ConfigurationKeyName("SIM_CAPACITY_AWARE")] public bool CapacityAware { get; set; }

    [ConfigurationKeyName("SIM_OVERLOAD_THRESHOLD")] public double? OverloadThreshold { get; set; }

    // SimAgentFamily.

    [ConfigurationKeyName("SIM_AGENT_COUNT")] public int AgentCount { get; set; } = 20;

    /// <summary>Per-agent memory weight, for simulating overload scenarios (GH-3959).</summary>
    [ConfigurationKeyName("SIM_AGENT_MB")] public int AgentMb { get; set; }

    /// <summary>
    /// Per-agent startup delay, mimicking projection agents that catch up before they're
    /// "running" — the slow starts that stretch GH-3987's rollout overlap windows.
    /// </summary>
    [ConfigurationKeyName("SIM_START_DELAY_MS")] public int StartDelayMs { get; set; }

    // Pod identity, from the downward API in k8s/churnsim.yaml.

    [ConfigurationKeyName("POD_NAME")] public string PodName { get; set; } = Environment.MachineName;

    [ConfigurationKeyName("POD_IP")] public string PodIp { get; set; } = "unknown";
}
