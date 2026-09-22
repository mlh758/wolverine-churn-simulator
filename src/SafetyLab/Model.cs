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

    /// <summary>
    /// The second RDBMS arm. Structurally the same as <see cref="Postgres"/> from out here — a
    /// session-scoped lock whose holder is a server-side connection, so a sample carries
    /// <see cref="LockRow"/>s and not compare-exchange values — which is the whole reason it
    /// needed no second copy of any checker.
    /// </summary>
    public const string MySql = "mysql";

    public const string RavenDb = "ravendb";

    /// <summary>Every value <c>SIM_BACKEND</c> and <c>--backend</c> accept.</summary>
    public static readonly string[] All = [Postgres, MySql, RavenDb];

    /// <summary>
    /// Backends whose leadership lock belongs to a SESSION, so that the death of the session is
    /// the release and the lock can never outlive its holder. The distinction the checkers care
    /// about is this one and not the product name: S8 (an expired-but-unclaimed lock) is
    /// meaningless here, and S11 (a holder that stopped heartbeating) only means anything here.
    /// </summary>
    public static bool IsSessionLock(string backend) => backend is Postgres or MySql;
}

/// <summary>
/// First line of history.jsonl. Records what the monitor was actually watching.
///
/// <paramref name="Backend"/> and <paramref name="LockKey"/> were added with the RavenDB arm and
/// default to null so that every run directory captured before them still loads and still checks:
/// a history with no backend is a PostgreSQL history, which is what all of them were.
/// <paramref name="Replicas"/> (schema version 3) lists every RavenDB member url the monitor
/// read; null means a single store endpoint, which is every capture before the replicated arm.
/// </summary>
public record MetaRecord(
    DateTimeOffset StartedUtc,
    int TickMs,
    long LeaderLockId,
    string Schema,
    string ServerVersion,
    string? Backend = null,
    string? LockKey = null,
    IReadOnlyList<string>? Replicas = null)
{
    public string Kind => "meta";
    public int SchemaVersion => 3;

    public string BackendName => string.IsNullOrWhiteSpace(Backend) ? Backends.Postgres : Backend;
}

/// <summary>
/// One session-scoped lock held on the store, with whatever the server knows about its holder.
/// Locks other than the leader lock are captured too — Wolverine takes them for durability
/// agents and for schema migration, and a run where those pile up is worth seeing.
///
/// On PostgreSQL this is a row of <c>pg_locks</c> where <c>locktype = 'advisory'</c>, joined to
/// <c>pg_stat_activity</c>. On MySQL it is a <c>GET_LOCK</c> named lock, which the server models
/// differently enough to be worth spelling out field by field:
///
/// <list type="bullet">
///   <item><paramref name="Pid"/> — the MySQL connection id, i.e. what <c>KILL</c> takes.</item>
///   <item>
///     <paramref name="ObjId"/> — the integer parsed out of the lock's name. Wolverine names its
///     named locks <c>wolverine_&lt;schemaName.GetDeterministicHashCode()&gt;</c>, the same id
///     PostgreSQL passes to <c>pg_try_advisory_lock</c>, so
///     <c>RunHistory.LeaderLockHolders</c> matches on this column on both and needs no branch.
///   </item>
///   <item>
///     <paramref name="ApplicationName"/> — the lock's NAME (<c>wolverine_-1234567</c>). MySQL has
///     no <c>application_name</c>; the name is the better thing to carry in this column, because
///     it is what a reader would go and query for.
///   </item>
///   <item><paramref name="ClientAddr"/> — <c>PROCESSLIST_HOST</c>, which is the pod IP so long as
///     the server runs with <c>--skip-name-resolve</c> (k8s/mysql.yaml says why).</item>
///   <item><paramref name="BackendState"/> — the connection's command and state.</item>
///   <item>
///     <paramref name="ClassId"/> and <paramref name="ObjSubId"/> are PostgreSQL's two-part
///     advisory key and stay 0 here; <paramref name="BackendStart"/> is derived from
///     <c>PROCESSLIST_TIME</c>, which counts seconds in the current state rather than since the
///     connection opened, so treat it as "not older than".
///   </item>
/// </list>
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
///
/// <paramref name="Node"/> names the RavenDB server this row was read FROM, and is null on a
/// single-node capture. On a replicated store the same key is read from every member on every
/// tick, and the whole point of doing so is that under a network partition the members disagree:
/// a minority node keeps serving the last value it committed while the majority moves on. Two
/// rows with the same key and different owners, each tagged with its source, is what a split
/// looks like from outside. <c>RunHistory.LeaderHolders</c> collapses agreeing replicas into one
/// holder so a healthy three-node read is one leader, not three.
/// </summary>
public record CompareExchangeRow(
    string Key,
    Guid? NodeId,
    DateTimeOffset? ExpiresAt,
    long Index,
    string? Node = null);

/// <summary>
/// One RavenDB cluster member's view of the store on one tick, for the replicated arm (E7).
///
/// The sample's top-level <c>Nodes</c> and <c>Assignments</c> come from the FIRST configured url
/// (the primary view) exactly as on a single-node capture, so every existing checker runs
/// unchanged. What a replica view adds is the difference: <paramref name="Divergent"/> lists the
/// assignment rows this member holds with a different owner than the primary (or that the
/// primary lacks), and <paramref name="Missing"/> the agent uris the primary places that this
/// member has no document for. Both are empty on a healthy cluster within replication lag, which
/// is milliseconds; either staying non-empty is S9. Assignments are plain single-node writes with
/// asynchronous multi-master replication, so a partition is exactly where this diverges — and it
/// is stored as a diff rather than a second copy because 500 assignment rows per member per second
/// would triple the history for no information.
///
/// <paramref name="RaftLeader"/> and <paramref name="RaftState"/> are the member's own view of the
/// Raft cluster (<c>/cluster/topology</c>): a partitioned minority member reports no leader and a
/// Candidate/Follower state, which is how P1 proves the partition actually took rather than
/// assuming it did. <paramref name="Conflicts"/> is the database's <c>CountOfConflicts</c>: two
/// members accepting different writes to the same document produce one the moment they
/// re-replicate, unless a resolver silently picks a winner first. S10 reads it.
///
/// <paramref name="Error"/> is set and the rest left empty when this member could not be read.
/// That is coverage (the monitor's problem), not divergence (the store's), and C0 reports it.
/// </summary>
public record ReplicaView(
    string Url,
    string? NodeTag,
    string? RaftLeader,
    string? RaftState,
    long? Conflicts,
    int NodeCount,
    int AssignmentCount,
    IReadOnlyList<AssignmentRow> Divergent,
    IReadOnlyList<string> Missing,
    string? Error = null);

public record NodeRow(Guid Id, int NodeNumber, DateTimeOffset HealthCheck, string Description);

public record AssignmentRow(string Id, Guid NodeId, DateTimeOffset Started);

/// <summary>
/// One sampling tick. <paramref name="Error"/> is set instead of the payload when the tick
/// failed: a hole in the history has to be visible, because "no violation observed" across a
/// silent 30-second gap is not a result.
///
/// Leadership evidence is backend-shaped: the two RDBMS arms fill <paramref name="Locks"/> from
/// <c>pg_locks</c> / <c>performance_schema.metadata_locks</c>, RavenDB fills
/// <paramref name="Cmpxchg"/> from the compare-exchange store.
/// Both are optional and both arrive as empty on the other backend; read them through
/// <c>RunHistory.LeaderHolders</c> rather than directly, so a checker does not have to know.
///
/// <paramref name="Warnings"/> is for a tick that <em>succeeded but is not fully trustworthy</em>
/// — a RavenDB query whose result set hit the monitor's page limit, or one the server reported as
/// served from a stale index, or a MySQL tick where <c>IS_USED_LOCK</c> found the leadership lock
/// held and <c>performance_schema.metadata_locks</c> listed no such lock (the mdl instrument is
/// off, so this sample's view of every OTHER lock is blind). None is an <paramref name="Error"/>
/// (there is real data here) and none may be silent (the data may be short). The coverage check
/// reports them.
///
/// <paramref name="Replicas"/> is present only on a replicated RavenDB capture: one
/// <see cref="ReplicaView"/> per cluster member, including the primary. Null on every earlier
/// history, which loads and checks exactly as before.
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
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<ReplicaView>? Replicas = null)
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
