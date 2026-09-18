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
/// </list>
/// </summary>
public sealed class RavenClusterMonitor : MonitorLoop
{
    private readonly RavenClient _client;
    private readonly string _serviceName;
    private readonly int _pageSize;
    private readonly TimeSpan _tick;

    /// <summary>
    /// Wolverine's own compare-exchange keys, and nothing else. RavenDB's cluster-wide
    /// transactions write <c>rvn-atomic/&lt;doc id&gt;</c> entries into the same store — one per
    /// node registration here — and capturing those would bury the two keys that matter.
    /// </summary>
    private const string WolverineKeyPrefix = "wolverine/";

    public RavenClusterMonitor(RavenClient client, string serviceName, int pageSize, TimeSpan tick, TextWriter output)
        : base(tick, output)
    {
        _client = client;
        _serviceName = serviceName;
        _pageSize = pageSize;
        _tick = tick;
    }

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
            _client.Database,
            await _client.BuildVersionAsync(token),
            Backends.RavenDb,
            $"wolverine/leader/{_serviceName.ToLowerInvariant()}");
    }

    protected override async Task<Sample> TakeSampleAsync(long seq, DateTimeOffset started, CancellationToken token)
    {
        var warnings = new List<string>();

        var cmpxchg = await _client.CompareExchangeAsync(WolverineKeyPrefix, _pageSize, token);
        if (cmpxchg.Count >= _pageSize)
        {
            warnings.Add($"compare-exchange read hit the {_pageSize}-key page limit");
        }

        // Both of these are collection queries with no where/order-by, which RavenDB serves
        // straight from the collection rather than from an auto-index (IndexName comes back as
        // "collection/X"). That is why no WaitForNonStaleResults is asked for: there is no index
        // to be stale. IsStale is still recorded, because the day that stops being true is
        // exactly the day this monitor must not be believed.
        var nodeQuery = await _client.QueryAsync($"from WolverineNodes limit 0, {_pageSize}", token);
        var assignmentQuery = await _client.QueryAsync($"from AgentAssignments limit 0, {_pageSize}", token);

        note(warnings, nodeQuery, "WolverineNodes");
        note(warnings, assignmentQuery, "AgentAssignments");

        var nodes = nodeQuery.Results.Select(readNode).Where(x => x is not null).Select(x => x!).ToList();
        var assignments = assignmentQuery.Results
            .Select(x => readAssignment(x, started))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        return new Sample(seq, started, assignmentQuery.ServerDate ?? nodeQuery.ServerDate, 0,
            [], nodes, assignments, null, cmpxchg, warnings.Count > 0 ? warnings : null);
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
