using System.Text.Json;

namespace SafetyLab;

/// <summary>
/// The RavenDB arm of the monitor: same three pieces of server-side truth as
/// <see cref="ClusterMonitor"/>, read from a store that keeps them in a materially different
/// shape, and that is the reason to run this arm at all.
///
/// <list type="number">
///   <item>
///     <b>The lock.</b> PostgreSQL leadership is a session-scoped advisory lock — held by a
///     backend, released by that backend's death, with the server as the actor. RavenDB
///     leadership is a compare-exchange document with a five-minute <c>ExpirationTime</c>
///     (<c>RavenDbMessageStore.Locking</c>). Nobody in the server clears it. The only route back
///     to an elected leader after an ungraceful death is a peer running
///     <c>tryTakeOverIfExpiredAsync</c>. That turns "lock held by a departed node" from a bug
///     fingerprint (Postgres, where it should be impossible) into an expected transient with a
///     five-minute ceiling — so S4 is read here alongside S8, which measures the stall.
///   </item>
///   <item>
///     <b>The reads.</b> The node registry and the assignment set are queries, not SELECTs.
///     They come back with an <c>IsStale</c> flag and a page limit, neither of which a SQL read
///     has to think about. Both are recorded on every sample, because a short or stale read that
///     nothing reports is how a harness says "no violations" about data it never saw.
///   </item>
///   <item>
///     <b>The clock.</b> There is no <c>now()</c>. The server's clock comes from the HTTP
///     <c>Date</c> response header, which is second-resolution — enough to catch the
///     app-clock-versus-store-clock skew that staleness detection depends on, not enough to
///     quote in milliseconds. C0 says so in the report rather than leaving it to be assumed.
///   </item>
///   <item>
///     <b>The members.</b> On a replicated store (E7) every one of the above is read from EVERY
///     cluster member, every tick, and not from the service endpoint. The service would answer
///     from whichever member the round-robin lands on, which under a partition means the history
///     flickers between two realities and neither checker can tell which it is looking at. The
///     first configured url is the <em>primary</em> view and fills the sample's top-level fields
///     exactly as a single-node capture does; every member — the primary included — also gets a
///     <see cref="ReplicaView"/> carrying its own Raft view, conflict count and the assignment
///     rows on which it disagrees with the primary. Compare-exchange rows are captured from every
///     member and tagged with their source, because "which member says who leads" is the split.
///   </item>
/// </list>
/// </summary>
public sealed class RavenClusterMonitor : MonitorLoop
{
    private readonly IReadOnlyList<RavenClient> _clients;
    private readonly string _serviceName;
    private readonly int _pageSize;
    private readonly TimeSpan _tick;

    /// <summary>
    /// Wolverine's own compare-exchange keys, and nothing else. RavenDB's cluster-wide
    /// transactions write <c>rvn-atomic/&lt;doc id&gt;</c> entries into the same store — one per
    /// node registration here — and capturing those would bury the two keys that matter.
    /// </summary>
    private const string WolverineKeyPrefix = "wolverine/";

    public RavenClusterMonitor(IReadOnlyList<RavenClient> clients, string serviceName, int pageSize, TimeSpan tick,
        TextWriter output)
        : base(tick, output)
    {
        if (clients.Count == 0) throw new ArgumentException("at least one RavenDB url is required", nameof(clients));

        _clients = clients;
        _serviceName = serviceName;
        _pageSize = pageSize;
        _tick = tick;
    }

    private RavenClient Primary => _clients[0];

    private bool Replicated => _clients.Count > 1;

    protected override async Task<MetaRecord> DescribeAsync(CancellationToken token)
    {
        // LeaderLockId is a PostgreSQL concept (a schema-name hash) with no RavenDB counterpart,
        // so it is recorded as 0 rather than as a plausible-looking number. `Schema` carries the
        // database name, which is the nearest thing this backend has to the same question, and
        // LockKey carries the key the store settles on once StartScheduledJobs has run.
        return new MetaRecord(
            DateTimeOffset.UtcNow,
            (int)_tick.TotalMilliseconds,
            0,
            Primary.Database,
            await Primary.BuildVersionAsync(token),
            Backends.RavenDb,
            $"wolverine/leader/{_serviceName.ToLowerInvariant()}",
            Replicated ? _clients.Select(x => x.Url).ToList() : null);
    }

    /// <summary>Everything one member answered on one tick, before it is folded into the sample.</summary>
    private sealed record MemberRead(
        RavenClient Client,
        IReadOnlyList<CompareExchangeRow> Cmpxchg,
        RavenQueryResult? NodeQuery,
        RavenQueryResult? AssignmentQuery,
        ClusterView? Cluster,
        long? Conflicts,
        Exception? Error);

    protected override async Task<Sample> TakeSampleAsync(long seq, DateTimeOffset started, CancellationToken token)
    {
        // Every member in parallel, so a three-member sample costs one round trip and not three,
        // and so the members' answers are as close to simultaneous as the monitor can make them.
        // A member that fails is recorded as failed and the others are kept: during a partition
        // the monitor is deliberately on no side of the cut, but a member that IS unreachable
        // is a hole in that member's column and nothing else.
        var reads = await Task.WhenAll(_clients.Select(c => readMemberAsync(c, token)));

        var primary = reads[0];
        if (primary.Error is not null)
        {
            // The primary is what fills the checker-facing fields, so a failed primary is a failed
            // tick — the same as a single-node capture, and MonitorLoop records the hole.
            throw primary.Error;
        }

        var warnings = new List<string>();

        if (primary.Cmpxchg.Count >= _pageSize)
        {
            warnings.Add($"compare-exchange read hit the {_pageSize}-key page limit");
        }

        // Both of these are collection queries with no where/order-by, which RavenDB serves
        // straight from the collection rather than from an auto-index (IndexName comes back as
        // "collection/X"). That is why no WaitForNonStaleResults is asked for: there is no index
        // to be stale. IsStale is still recorded, because the day that stops being true is
        // exactly the day this monitor must not be believed.
        note(warnings, primary.NodeQuery!, "WolverineNodes");
        note(warnings, primary.AssignmentQuery!, "AgentAssignments");

        var nodes = readNodes(primary.NodeQuery!);
        var assignments = readAssignments(primary.AssignmentQuery!, started);

        // Lock rows from every member, each tagged with where it was read. On a single-node
        // capture the tag is left null so those histories look exactly as they always did.
        var cmpxchg = Replicated
            ? reads.Where(r => r.Error is null)
                .SelectMany(r => r.Cmpxchg.Select(row => row with { Node = r.Client.Url }))
                .ToList()
            : primary.Cmpxchg;

        IReadOnlyList<ReplicaView>? replicas = null;
        if (Replicated)
        {
            var primaryOwners = assignments.ToDictionary(x => x.Id, x => x.NodeId);
            replicas = reads.Select(r => describeMember(r, primaryOwners, started)).ToList();
        }

        return new Sample(seq, started, primary.AssignmentQuery!.ServerDate ?? primary.NodeQuery!.ServerDate, 0,
            [], nodes, assignments, null, cmpxchg, warnings.Count > 0 ? warnings : null, replicas);
    }

    private async Task<MemberRead> readMemberAsync(RavenClient client, CancellationToken token)
    {
        try
        {
            var cmpxchg = client.CompareExchangeAsync(WolverineKeyPrefix, _pageSize, token);
            var nodeQuery = client.QueryAsync($"from WolverineNodes limit 0, {_pageSize}", token);
            var assignmentQuery = client.QueryAsync($"from AgentAssignments limit 0, {_pageSize}", token);

            // The cluster view and the conflict count only mean something with more than one
            // member, and they are two more round trips per tick, so a single-node capture does
            // not pay for them.
            var cluster = Replicated
                ? client.ClusterTopologyAsync(token).ContinueWith(t => (ClusterView?)t.Result, token)
                : Task.FromResult<ClusterView?>(null);
            var conflicts = Replicated ? client.ConflictCountAsync(token) : Task.FromResult(0L);

            await Task.WhenAll(cmpxchg, nodeQuery, assignmentQuery, cluster, conflicts);

            return new MemberRead(client, await cmpxchg, await nodeQuery, await assignmentQuery,
                await cluster, Replicated ? await conflicts : null, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new MemberRead(client, [], null, null, null, null, e);
        }
    }

    /// <summary>
    /// A member's view relative to the primary's. Rows are compared by agent uri and owner: a row
    /// this member has with a different owner, or one the primary does not have at all, is
    /// <c>Divergent</c>; a row the primary has and this member does not is <c>Missing</c>. For the
    /// primary itself both are empty by construction, which is the row that pins the comparison.
    /// </summary>
    private static ReplicaView describeMember(MemberRead read, IReadOnlyDictionary<string, Guid> primaryOwners,
        DateTimeOffset started)
    {
        if (read.Error is not null)
        {
            return new ReplicaView(read.Client.Url, null, null, null, null, 0, 0, [], [],
                $"{read.Error.GetType().Name}: {read.Error.Message}");
        }

        var nodes = readNodes(read.NodeQuery!);
        var assignments = readAssignments(read.AssignmentQuery!, started);

        var divergent = new List<AssignmentRow>();
        var seen = new HashSet<string>();

        foreach (var row in assignments)
        {
            seen.Add(row.Id);
            if (!primaryOwners.TryGetValue(row.Id, out var owner) || owner != row.NodeId)
            {
                divergent.Add(row);
            }
        }

        var missing = primaryOwners.Keys.Where(id => !seen.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new ReplicaView(
            read.Client.Url,
            read.Cluster?.NodeTag,
            read.Cluster?.Leader,
            read.Cluster?.State,
            read.Conflicts,
            nodes.Count,
            assignments.Count,
            divergent,
            missing);
    }

    private static void note(List<string> warnings, RavenQueryResult result, string collection)
    {
        if (result.Truncated)
        {
            warnings.Add($"{collection} read returned {result.Results.Count} of {result.TotalResults} — page limit hit");
        }

        if (result.IsStale)
        {
            warnings.Add($"{collection} read was served from a stale index ({result.IndexName})");
        }
    }

    private static List<NodeRow> readNodes(RavenQueryResult query)
        => query.Results.Select(readNode).Where(x => x is not null).Select(x => x!).ToList();

    private static List<AssignmentRow> readAssignments(RavenQueryResult query, DateTimeOffset fallback)
        => query.Results.Select(x => readAssignment(x, fallback)).Where(x => x is not null).Select(x => x!).ToList();

    /// <summary>
    /// A <c>WolverineNodes</c> document. <c>Description</c> is <c>Environment.MachineName</c>,
    /// which inside a pod is the pod name — the same join key the PostgreSQL arm uses to tie an
    /// assignment back to a pod's log stream, so S6/S7 need no RavenDB-specific handling.
    /// </summary>
    private static NodeRow? readNode(JsonElement doc)
    {
        if (!TryGuid(doc, "NodeId", out var id)) return null;

        return new NodeRow(
            id,
            doc.TryGetProperty("AssignedNodeNumber", out var number) && number.TryGetInt32(out var n) ? n : 0,
            TryTime(doc, "LastHealthCheck") ?? default,
            doc.TryGetProperty("Description", out var description) ? description.GetString() ?? "" : "");
    }

    /// <summary>
    /// An <c>AgentAssignments</c> document. The identifier used by every checker is the
    /// <c>AgentUri</c> field ("sim://agent1/", "wolverine://leader/"), NOT the document id: Raven
    /// mangles the uri into a legal id ("sim__agent1") and a checker comparing against that would
    /// silently match nothing — the same class of mistake that made the first live PostgreSQL run
    /// pass every leader-side check for want of a row to fail on.
    /// </summary>
    private static AssignmentRow? readAssignment(JsonElement doc, DateTimeOffset fallback)
    {
        if (!doc.TryGetProperty("AgentUri", out var uri) || uri.GetString() is not { } agentUri) return null;
        if (!TryGuid(doc, "NodeId", out var nodeId)) return null;

        // `started` on the Postgres table has no field here; the document's last write is the
        // nearest true statement. Nothing checks it, and it is kept only so the two histories
        // carry the same columns.
        var lastModified = doc.TryGetProperty("@metadata", out var metadata)
            ? TryTime(metadata, "@last-modified")
            : null;

        return new AssignmentRow(agentUri, nodeId, lastModified ?? fallback);
    }

    private static bool TryGuid(JsonElement element, string property, out Guid value)
    {
        value = default;
        return element.TryGetProperty(property, out var raw) && Guid.TryParse(raw.GetString(), out value);
    }

    private static DateTimeOffset? TryTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var raw) && raw.TryGetDateTimeOffset(out var value)
            ? value
            : null;
}
