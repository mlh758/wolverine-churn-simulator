using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SafetyLab;

/// <summary>One RQL query's answer, plus the two things about it that are not the rows.</summary>
public sealed record RavenQueryResult(
    IReadOnlyList<JsonElement> Results,
    long TotalResults,
    bool IsStale,
    string IndexName,
    DateTimeOffset? ServerDate)
{
    /// <summary>True when the server had more rows than the page this query asked for.</summary>
    public bool Truncated => Results.Count < TotalResults;
}

/// <summary>
/// One server's answer to <c>/cluster/topology</c>: who it thinks it is, who it thinks leads, and
/// which servers it thinks are members. Parsed leniently — a field RavenDB stops sending becomes
/// null, never an exception, because the monitor must keep sampling through whatever the cluster
/// does to itself.
/// </summary>
public sealed record ClusterView(
    string? NodeTag,
    string? Leader,
    string? State,
    IReadOnlyDictionary<string, string> Members)
{
    public static ClusterView Parse(JsonElement root)
    {
        var members = new Dictionary<string, string>();

        if (root.TryGetProperty("Topology", out var topology) &&
            topology.TryGetProperty("Members", out var raw) &&
            raw.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in raw.EnumerateObject())
            {
                members[member.Name] = member.Value.GetString() ?? "";
            }
        }

        return new ClusterView(
            str(root, "NodeTag"),
            str(root, "Leader"),
            str(root, "CurrentState"),
            members);

        static string? str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// The RavenDB half of "an outside observer with no Wolverine dependency".
///
/// Deliberately raw HTTP against the documented REST surface rather than <c>RavenDB.Client</c>.
/// The monitor must be able to watch a cluster running any Wolverine version without sharing a
/// line of code — or a serialization convention — with it, and a typed client would quietly
/// re-impose the app's own view of these documents on the observer. The three endpoints used here
/// (<c>/queries</c>, <c>/cmpxchg</c>, <c>/build/version</c>) are stable public API.
/// </summary>
public sealed class RavenClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _database;

    public RavenClient(string url, string database, TimeSpan timeout)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(url.TrimEnd('/') + "/"),
            Timeout = timeout
        };
        _database = database;
    }

    public string Database => _database;

    /// <summary>The server this client talks to, as configured — what a replica view is tagged with.</summary>
    public string Url => _http.BaseAddress!.ToString().TrimEnd('/');

    /// <summary>
    /// Run an RQL query. Callers pass their own <c>limit</c>: paging is the caller's problem
    /// because whether a short read matters depends on what is being read, and
    /// <see cref="RavenQueryResult.Truncated"/> makes it impossible to ignore either way.
    /// </summary>
    public async Task<RavenQueryResult> QueryAsync(string rql, CancellationToken token,
        bool waitForNonStaleResults = false)
    {
        var body = new Dictionary<string, object>
        {
            ["Query"] = rql
        };

        if (waitForNonStaleResults)
        {
            body["WaitForNonStaleResults"] = true;
            body["WaitForNonStaleResultsTimeoutInSec"] = 15;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"databases/{_database}/queries")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, token);
        await throwOnErrorAsync(response, rql, token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;

        var results = root.TryGetProperty("Results", out var rows)
            ? rows.EnumerateArray().Select(x => x.Clone()).ToList()
            : [];

        return new RavenQueryResult(
            results,
            root.TryGetProperty("TotalResults", out var total) ? total.GetInt64() : results.Count,
            root.TryGetProperty("IsStale", out var stale) && stale.GetBoolean(),
            root.TryGetProperty("IndexName", out var index) ? index.GetString() ?? "" : "",
            response.Headers.Date);
    }

    /// <summary>
    /// Every compare-exchange value under a key prefix. RavenDB keeps its own bookkeeping in this
    /// same store under <c>rvn-atomic/</c> (one entry per document touched by a cluster-wide
    /// transaction, and Wolverine's node registration uses those), so a prefix is not an
    /// optimisation — an unfiltered read is mostly Raven's own noise.
    /// </summary>
    public async Task<IReadOnlyList<CompareExchangeRow>> CompareExchangeAsync(string prefix, int pageSize,
        CancellationToken token)
    {
        var url = $"databases/{_database}/cmpxchg?startsWith={Uri.EscapeDataString(prefix)}&start=0&pageSize={pageSize}";

        using var response = await _http.GetAsync(url, token);
        await throwOnErrorAsync(response, url, token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var rows = new List<CompareExchangeRow>();

        if (!doc.RootElement.TryGetProperty("Results", out var results)) return rows;

        foreach (var entry in results.EnumerateArray())
        {
            var key = entry.TryGetProperty("Key", out var k) ? k.GetString() ?? "" : "";
            var index = entry.TryGetProperty("Index", out var i) ? i.GetInt64() : -1;

            // The lock payload is nested: { "Value": { "Object": { NodeId, ExpirationTime } } }.
            // Anything that does not have that shape is captured with nulls rather than dropped —
            // a compare-exchange key under wolverine/ that is NOT a DistributedLock is itself
            // worth seeing in the history.
            Guid? nodeId = null;
            DateTimeOffset? expires = null;

            if (entry.TryGetProperty("Value", out var value) &&
                value.TryGetProperty("Object", out var payload) &&
                payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("NodeId", out var n) && Guid.TryParse(n.GetString(), out var parsed))
                {
                    nodeId = parsed;
                }

                if (payload.TryGetProperty("ExpirationTime", out var e) &&
                    e.TryGetDateTimeOffset(out var parsedExpiry))
                {
                    expires = parsedExpiry;
                }
            }

            rows.Add(new CompareExchangeRow(key, nodeId, expires, index));
        }

        return rows;
    }

    /// <summary>
    /// Delete every document a query matches, and wait for it. RavenDB answers the request with
    /// an operation id and does the work in the background, so returning at that point would let
    /// a caller start the next measurement window while the previous one is still being cleared —
    /// which is the RavenDB shape of the reset-then-measure race.
    /// </summary>
    public async Task<long> DeleteByQueryAsync(string rql, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"databases/{_database}/queries")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Query = rql }), Encoding.UTF8,
                "application/json")
        };

        using var response = await _http.SendAsync(request, token);
        await throwOnErrorAsync(response, rql, token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("OperationId", out var id)) return 0;

        return await awaitOperationAsync(id.GetInt64(), token);
    }

    private async Task<long> awaitOperationAsync(long operationId, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await _http.GetAsync(
                $"databases/{_database}/operations/state?id={operationId}", token);
            await throwOnErrorAsync(response, $"operation {operationId}", token);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var status = doc.RootElement.TryGetProperty("Status", out var s) ? s.GetString() : null;

            if (status is "Completed")
            {
                return doc.RootElement.TryGetProperty("Result", out var result) &&
                       result.TryGetProperty("Total", out var total) && total.TryGetInt64(out var value)
                    ? value
                    : 0;
            }

            if (status is "Faulted" or "Canceled")
            {
                throw new InvalidOperationException($"RavenDB operation {operationId} ended as {status}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }

        throw new TimeoutException($"RavenDB operation {operationId} did not complete within five minutes");
    }

    /// <summary>
    /// Hard-delete the database. The RavenDB equivalent of <c>drop schema … cascade</c>: what you
    /// do between runs of different Wolverine builds so that one build's documents cannot be
    /// mistaken for the next one's.
    /// </summary>
    public async Task DropDatabaseAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "admin/databases")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { DatabaseNames = new[] { _database }, HardDelete = true }),
                Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, token);
        await throwOnErrorAsync(response, $"drop database {_database}", token);
    }

    // ------------------------------------------------------------ cluster reads

    /// <summary>
    /// The database's own statistics. Only <c>CountOfConflicts</c> is read: on a replicated
    /// database it is the number of documents that currently have more than one version because
    /// two members accepted different writes, and it is the cheapest server-side signal that
    /// the assignment plane's claim-if-absent guard has been defeated by replication.
    /// </summary>
    public async Task<long> ConflictCountAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync($"databases/{_database}/stats", token);
        await throwOnErrorAsync(response, "database stats", token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return doc.RootElement.TryGetProperty("CountOfConflicts", out var count) && count.TryGetInt64(out var value)
            ? value
            : 0;
    }

    /// <summary>
    /// This member's view of the Raft cluster. Deliberately THIS member's: under a partition the
    /// answer differs by member, and that difference is the finding. A minority member reports no
    /// leader, and its state falls out of Leader/Follower into Candidate.
    /// </summary>
    public async Task<ClusterView> ClusterTopologyAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync("cluster/topology", token);
        await throwOnErrorAsync(response, "cluster topology", token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return ClusterView.Parse(doc.RootElement);
    }

    /// <summary>
    /// The documents currently in conflict, with the change vector and owner of every version.
    /// RavenDB keeps the versions side by side until a resolver (or a client) picks one; if the
    /// database has a resolver that runs first, this is empty and the evidence has moved to the
    /// resolved-conflict revisions instead — read both.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ConflictsAsync(int pageSize, CancellationToken token)
    {
        using var response = await _http.GetAsync(
            $"databases/{_database}/replication/conflicts?start=0&pageSize={pageSize}", token);
        await throwOnErrorAsync(response, "replication conflicts", token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return doc.RootElement.TryGetProperty("Results", out var results)
            ? results.EnumerateArray().Select(x => x.Clone()).ToList()
            : [];
    }

    /// <summary>
    /// Conflicts the server has already resolved since a point in time. RavenDB stores the losing
    /// versions as revisions flagged <c>Conflicted</c>/<c>Resolved</c> whether or not revisions
    /// are otherwise enabled, so a conflict that a resolver picked a winner for in the same tick
    /// still leaves this trace behind. What the resolver picked, and by what rule, is part of E7's
    /// result — neither Wolverine nor this rig configures one.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ResolvedConflictsAsync(DateTimeOffset since, int pageSize,
        CancellationToken token)
    {
        using var response = await _http.GetAsync(
            $"databases/{_database}/revisions/resolved?since={Uri.EscapeDataString(since.UtcDateTime.ToString("O"))}&take={pageSize}",
            token);
        await throwOnErrorAsync(response, "resolved conflict revisions", token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return doc.RootElement.TryGetProperty("Results", out var results)
            ? results.EnumerateArray().Select(x => x.Clone()).ToList()
            : [];
    }

    /// <summary>The database group: which members hold this database, and as what role.</summary>
    public async Task<IReadOnlyList<(string Tag, string Url, string Role)>> DatabaseTopologyAsync(
        CancellationToken token)
    {
        using var response = await _http.GetAsync($"topology?name={Uri.EscapeDataString(_database)}", token);
        await throwOnErrorAsync(response, "database topology", token);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var rows = new List<(string, string, string)>();

        if (!doc.RootElement.TryGetProperty("Nodes", out var nodes)) return rows;

        foreach (var node in nodes.EnumerateArray())
        {
            rows.Add((
                node.TryGetProperty("ClusterTag", out var tag) ? tag.GetString() ?? "?" : "?",
                node.TryGetProperty("Url", out var url) ? url.GetString() ?? "?" : "?",
                node.TryGetProperty("ServerRole", out var role) ? role.GetString() ?? "?" : "?"));
        }

        return rows;
    }

    // ----------------------------------------------------------- cluster admin

    /// <summary>
    /// Turn a passive, freshly started server into a one-node cluster. A node in
    /// <c>Setup.Mode=None</c> stays passive until either this runs or another cluster's leader
    /// adds it, and every database operation against a passive node is a 503.
    /// </summary>
    public async Task BootstrapAsync(CancellationToken token)
    {
        using var response = await _http.PostAsync("admin/cluster/bootstrap", null, token);
        await throwOnErrorAsync(response, "cluster bootstrap", token);
    }

    /// <summary>Add another (passive) server to this member's cluster. Runs on the current leader.</summary>
    public async Task AddNodeAsync(string url, string tag, CancellationToken token)
    {
        using var response = await _http.PutAsync(
            $"admin/cluster/node?url={Uri.EscapeDataString(url)}&tag={Uri.EscapeDataString(tag)}&watcher=false",
            null, token);
        await throwOnErrorAsync(response, $"add cluster node {tag} ({url})", token);
    }

    public async Task<bool> DatabaseExistsAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync(
            $"admin/databases?name={Uri.EscapeDataString(_database)}", token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        await throwOnErrorAsync(response, "database record", token);
        return true;
    }

    /// <summary>
    /// Create the database across <paramref name="replicationFactor"/> members. ChurnSim creates
    /// it with factor 1 when it finds none, so on the replicated arm this must run BEFORE the
    /// first ChurnSim pod starts, or the "cluster" is three servers replicating nothing.
    /// </summary>
    public async Task CreateDatabaseAsync(int replicationFactor, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"admin/databases?name={Uri.EscapeDataString(_database)}&replicationFactor={replicationFactor}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { DatabaseName = _database, Settings = new Dictionary<string, string>() }),
                Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, token);
        await throwOnErrorAsync(response, $"create database {_database} (factor {replicationFactor})", token);
    }

    public async Task<string> BuildVersionAsync(CancellationToken token)
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<JsonElement>("build/version", token);
            var full = payload.TryGetProperty("FullVersion", out var v) ? v.GetString() : null;
            var build = payload.TryGetProperty("BuildVersion", out var b) ? b.GetInt32().ToString() : "?";
            return $"RavenDB {full ?? "unknown"} (build {build})";
        }
        catch (Exception e)
        {
            return $"unavailable at startup: {e.Message}";
        }
    }

    private static async Task throwOnErrorAsync(HttpResponseMessage response, string what, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;

        // RavenDB puts a genuinely useful message in the body. Losing it to a bare status code
        // turns "the database does not exist yet" into "400 Bad Request".
        var body = await response.Content.ReadAsStringAsync(token);
        var trimmed = body.Length > 400 ? body[..400] + "…" : body;
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} for {what}: {trimmed}");
    }

    public void Dispose() => _http.Dispose();
}
