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

/// <summary>The message store under test. Determines which leadership evidence a sample carries.</summary>
public static class Backends
{
    public const string Postgres = "postgres";
    public const string RavenDb = "ravendb";
}

/// <summary>
/// First line of history.jsonl. Records what the monitor was actually watching.
///
/// <paramref name="Backend"/> and <paramref name="LockKey"/> were added with the RavenDB arm and
/// default to null so that every run directory captured before them still loads and still checks:
/// a history with no backend is a PostgreSQL history, which is what all of them were. Schema
/// version 2.
/// </summary>
public record MetaRecord(
    DateTimeOffset StartedUtc,
    int TickMs,
    long LeaderLockId,
    string Schema,
    string ServerVersion,
    string? Backend = null,
    string? LockKey = null)
{
    public string Kind => "meta";
    public int SchemaVersion => 2;

    public string BackendName => string.IsNullOrWhiteSpace(Backend) ? Backends.Postgres : Backend;
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

/// <summary>
/// One RavenDB compare-exchange value under the <c>wolverine/</c> prefix — the RavenDB analogue
/// of a <see cref="LockRow"/>, and a structurally different animal.
///
/// A PostgreSQL advisory lock is held by a <em>session</em>: it names a backend, not a node, so
/// attributing it takes the identity map, and it disappears the moment that backend does. A
/// RavenDB lock is a <em>document</em> in the Raft cluster: it names its owner outright (no
/// identity map needed, so S3 and S4 work here without pod logs) and it survives its owner's
/// death until <paramref name="ExpiresAt"/> passes AND some peer troubles to CAS it away.
/// <c>RavenDbMessageStore.Locking</c> writes five-minute expirations, so that is the ceiling on
/// a failover stall — which is what S8 measures and what has no Postgres equivalent.
///
/// The keys that Wolverine writes are <c>wolverine/leader/&lt;service&gt;</c> and
/// <c>wolverine/scheduled/&lt;service&gt;</c>, but only after <c>StartScheduledJobs</c> runs;
/// the store's constructor initialises them to the un-suffixed <c>wolverine/leader</c> and
/// <c>wolverine/scheduled</c>. Both forms are captured deliberately: leadership split across the
/// two spellings would be invisible if the monitor watched only one, and it is exactly the shape
/// of bug the Postgres S1 sentinel was written to catch.
/// </summary>
public record CompareExchangeRow(
    string Key,
    Guid? NodeId,
    DateTimeOffset? ExpiresAt,
    long Index);

public record NodeRow(Guid Id, int NodeNumber, DateTimeOffset HealthCheck, string Description);

public record AssignmentRow(string Id, Guid NodeId, DateTimeOffset Started);

/// <summary>
/// One sampling tick. <paramref name="Error"/> is set instead of the payload when the tick
/// failed: a hole in the history has to be visible, because "no violation observed" across a
/// silent 30-second gap is not a result.
///
/// Leadership evidence is backend-shaped: PostgreSQL fills <paramref name="Locks"/> from
/// <c>pg_locks</c>, RavenDB fills <paramref name="Cmpxchg"/> from the compare-exchange store.
/// Both are optional and both arrive as empty on the other backend; read them through
/// <c>RunHistory.LeaderHolders</c> rather than directly, so a checker does not have to know.
///
/// <paramref name="Warnings"/> is for a tick that <em>succeeded but is not fully trustworthy</em>
/// — a RavenDB query whose result set hit the monitor's page limit, or one the server reported as
/// served from a stale index. Neither is an <paramref name="Error"/> (there is real data here) and
/// neither may be silent (the data may be short). The coverage check reports them.
/// </summary>
public record Sample(
    long Seq,
    DateTimeOffset Ts,
    DateTimeOffset? DbTs,
    double ElapsedMs,
    IReadOnlyList<LockRow> Locks,
    IReadOnlyList<NodeRow> Nodes,
    IReadOnlyList<AssignmentRow> Assignments,
    string? Error = null,
    IReadOnlyList<CompareExchangeRow>? Cmpxchg = null,
    IReadOnlyList<string>? Warnings = null)
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

/// <summary>
/// A control-plane log line from a node's Wolverine.Runtime.Agents.* categories — the leader
/// deciding placements, batches being dispatched and confirmed, leadership changing hands.
/// Captured because the samples show the assignment table's *state* but never say why it moved,
/// and "why did the last 29 agents take three and a half minutes" is not answerable from state.
/// </summary>
public record ControlEventRecord(DateTimeOffset Ts, string PodName, string Level, string Category, string Message)
{
    public string Kind => "control";
}

/// <summary>A phase marker written by the scripts, e.g. rollout-start / rollout-end.</summary>
public record MarkRecord(DateTimeOffset Ts, string Label)
{
    public string Kind => "mark";
}
