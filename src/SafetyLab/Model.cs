using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafetyLab;

/// <summary>
/// The on-disk history format. Everything the checkers see comes through these records,
/// and nothing else — a run directory is meant to outlive the cluster that produced it and
/// stay checkable by a later version of the checkers.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

/// <summary>First line of history.jsonl. Records what the monitor was actually watching.</summary>
public record MetaRecord(
    DateTimeOffset StartedUtc,
    int TickMs,
    long LeaderLockId,
    string Schema,
    string ServerVersion)
{
    public string Kind => "meta";
    public int SchemaVersion => 1;
}

/// <summary>
/// One row of <c>pg_locks</c> for an advisory lock, joined to its backend. Advisory locks
/// other than the leader lock are captured too — Wolverine takes them for durability agents
/// and for schema migration, and a run where those pile up is worth seeing.
/// </summary>
public record LockRow(
    int Pid,
    long ClassId,
    long ObjId,
    int ObjSubId,
    bool Granted,
    string? ApplicationName,
    string? ClientAddr,
    string? BackendState,
    DateTimeOffset? BackendStart);

public record NodeRow(Guid Id, int NodeNumber, DateTimeOffset HealthCheck, string Description);

public record AssignmentRow(string Id, Guid NodeId, DateTimeOffset Started);

/// <summary>
/// One sampling tick. <paramref name="Error"/> is set instead of the payload when the tick
/// failed: a hole in the history has to be visible, because "no violation observed" across a
/// silent 30-second gap is not a result.
/// </summary>
public record Sample(
    long Seq,
    DateTimeOffset Ts,
    DateTimeOffset? DbTs,
    double ElapsedMs,
    IReadOnlyList<LockRow> Locks,
    IReadOnlyList<NodeRow> Nodes,
    IReadOnlyList<AssignmentRow> Assignments,
    string? Error = null)
{
    public string Kind => "sample";
}

/// <summary>A node announcing itself, harvested from ChurnSim's startup log line.</summary>
public record IdentityRecord(DateTimeOffset Ts, Guid NodeId, string PodName, string PodIp)
{
    public string Kind => "identity";
}

/// <summary>An AGENT-START / AGENT-STOP line, harvested from a pod's logs.</summary>
public record AgentEventRecord(DateTimeOffset Ts, string Event, string AgentUri, string PodName)
{
    public string Kind => "agent";
}

/// <summary>A phase marker written by the scripts, e.g. rollout-start / rollout-end.</summary>
public record MarkRecord(DateTimeOffset Ts, string Label)
{
    public string Kind => "mark";
}
