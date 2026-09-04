using JasperFx;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace ChurnSim;

/// <summary>
///     Stand-in for something like Marten async projection/subscription distribution:
///     a fixed set of stateful agents that the Wolverine leader spreads across the
///     cluster with DistributeEvenly, exactly like the real projection agent family.
/// </summary>
public class SimAgentFamily : IStaticAgentFamily
{
    public const string SchemeName = "sim";

    private readonly ILogger<SimAgentFamily> _logger;
    private readonly int _count;
    private readonly int _agentMb;
    private readonly int _startDelay;

    public SimAgentFamily(ILogger<SimAgentFamily> logger)
    {
        _logger = logger;
        _count = int.TryParse(Environment.GetEnvironmentVariable("SIM_AGENT_COUNT"), out var c) ? c : 20;

        // Optional per-agent memory weight (MB) so overload scenarios (GH-3959)
        // can be simulated later by giving each running agent a real footprint
        _agentMb = int.TryParse(Environment.GetEnvironmentVariable("SIM_AGENT_MB"), out var mb) ? mb : 0;

        // Optional startup delay per agent, mimicking projection agents that need
        // to catch up before they're "running" -- the slow starts that stretch
        // GH-3987's rollout overlap windows
        _startDelay = int.TryParse(Environment.GetEnvironmentVariable("SIM_START_DELAY_MS"), out var d) ? d : 0;
    }

    public string Scheme => SchemeName;

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>(agentUris());
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        return new ValueTask<IAgent>(new SimAgent(uri, _logger, _agentMb, _startDelay));
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return new ValueTask<IReadOnlyList<Uri>>(agentUris());
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(SchemeName);
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<Uri> agentUris()
    {
        return Enumerable.Range(1, _count).Select(i => new Uri($"{SchemeName}://agent{i}")).ToList();
    }
}

public class SimAgent : IAgent
{
    private readonly ILogger _logger;
    private readonly int _agentMb;
    private readonly int _startDelayMs;
    private byte[]? _ballast;

    public SimAgent(Uri uri, ILogger logger, int agentMb, int startDelayMs)
    {
        Uri = uri;
        _logger = logger;
        _agentMb = agentMb;
        _startDelayMs = startDelayMs;
    }

    public Uri Uri { get; }
    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_startDelayMs > 0)
        {
            await Task.Delay(_startDelayMs, cancellationToken);
        }

        if (_agentMb > 0)
        {
            _ballast = new byte[_agentMb * 1024 * 1024];
            // touch every page so the memory is really resident
            for (var i = 0; i < _ballast.Length; i += 4096)
            {
                _ballast[i] = 1;
            }
        }

        Status = AgentStatus.Running;
        _logger.LogInformation("AGENT-START {AgentUri} at {Timestamp:O}", Uri, DateTimeOffset.UtcNow);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ballast = null;
        Status = AgentStatus.Stopped;
        _logger.LogInformation("AGENT-STOP {AgentUri} at {Timestamp:O}", Uri, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
