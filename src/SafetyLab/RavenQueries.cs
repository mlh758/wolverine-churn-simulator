using System.Text.Json;

namespace SafetyLab;

/// <summary>
/// The RavenDB stand-in for <c>psql -qAt -c "…"</c>.
///
/// The measurement scripts (measure.sh, duplicate-rate.sh, heal-test.sh) all work by shelling
/// into the database pod and getting tab-separated text back. RavenDB has no psql, and its
/// container has no shell client worth relying on, so the equivalent lives here and the scripts
/// reach it with <c>kubectl exec deploy/safetylab -- …</c>. That keeps the Postgres path
/// byte-for-byte unchanged — every number in RESULTS.md was taken through it — and it costs no
/// new image, because the monitor is already deployed and already speaks RavenDB's REST API.
///
/// Output is deliberately dumb TSV with no header, because that is what the scripts' existing
/// `cut`, `grep` and orphans.py consume.
/// </summary>
public static class RavenQueries
{
    /// <summary>
    /// Rows per request when walking a collection. Large enough that a 500-agent cluster's
    /// assignments come back in one round trip; small enough that a NodeRecords scan of a long
    /// run does not build one enormous response.
    /// </summary>
    private const int Page = 4096;

    /// <summary>
    /// A backstop on the NodeRecords scan. Hitting it is reported on stderr, never swallowed —
    /// a truncated count that reads like a total is the exact failure this repo's harness-traps
    /// list is made of.
    /// </summary>
    private const int MaxScan = 2_000_000;

    public static RavenClient Connect(string[] args)
    {
        var url = StringArg(args, "--url")
                  ?? Environment.GetEnvironmentVariable("RAVENDB_URL")
                  ?? "http://ravendb:8080";
        var database = StringArg(args, "--database")
                       ?? Environment.GetEnvironmentVariable("RAVENDB_DATABASE")
                       ?? "churnsim";

        return new RavenClient(url, database, TimeSpan.FromSeconds(30));
    }

    public static async Task<int> QueryAsync(string[] args)
    {
        // These run inside `kubectl exec` from shell scripts whose own error handling is a
        // non-zero exit and a line on stderr. A .NET stack trace in the middle of a measurement
        // script's output is noise that hides the one line that matters -- most often "the
        // database does not exist yet", which is an ordinary state right after a reset.
        try
        {
            return await runQueryAsync(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"query: {e.Message}");
            return 2;
        }
    }

    private static async Task<int> runQueryAsync(string[] args)
    {
        var kind = args.FirstOrDefault(x => !x.StartsWith('-'));
        if (kind is null)
        {
            Console.Error.WriteLine("query: a KIND is required " +
                                    "(assigned | placed | nodes | per-node | records | per-minute)");
            return 2;
        }

        using var client = Connect(args);
        var token = CancellationToken.None;

        switch (kind)
        {
            // agentUri <TAB> pod name, for every sim:// agent. The pod name comes from
            // WolverineNode.Description, which is Environment.MachineName and therefore the pod
            // name — the same join the PostgreSQL query makes through wolverine_nodes.description.
            case "assigned":
            {
                var pods = await podsByNodeAsync(client, token);
                foreach (var (agent, node) in await assignmentsAsync(client, token))
                {
                    if (!agent.StartsWith("sim://", StringComparison.Ordinal)) continue;
                    Console.WriteLine($"{agent}\t{(pods.TryGetValue(node, out var pod) ? pod : "unknown")}");
                }

                return 0;
            }

            case "placed":
            {
                var count = (await assignmentsAsync(client, token))
                    .Count(x => x.Agent.StartsWith("sim://", StringComparison.Ordinal));
                Console.WriteLine(count);
                return 0;
            }

            case "nodes":
            {
                foreach (var node in await nodesAsync(client, token))
                {
                    Console.WriteLine($"{node.Number}\t{node.Description}");
                }

                return 0;
            }

            // nodeId <TAB> agent count, the RavenDB shape of
            // "select node_id, count(*) from wolverine_node_assignments group by 1".
            case "per-node":
            {
                foreach (var group in (await assignmentsAsync(client, token))
                         .GroupBy(x => x.NodeId)
                         .OrderBy(x => x.Key))
                {
                    Console.WriteLine($"{group.Key}\t{group.Count()}");
                }

                return 0;
            }

            // RecordType <TAB> count, descending — the node_records churn report.
            case "records":
            {
                var (records, complete) = await scanRecordsAsync(client, token);
                foreach (var group in records.GroupBy(x => x.Type).OrderByDescending(x => x.Count()))
                {
                    Console.WriteLine($"{group.Key}\t{group.Count()}");
                }

                return complete ? 0 : 1;
            }

            // minute <TAB> count for one record type, to see churn concentrated in the rollout
            // window. Bucketed here rather than in RQL: RavenDB has no date_trunc, and scanning
            // the collection is a collection query that cannot come back stale, whereas a
            // where-clause would build an auto-index that can.
            case "per-minute":
            {
                var wanted = StringArg(args, "--event") ?? "AssignmentChanged";
                var (records, complete) = await scanRecordsAsync(client, token);

                foreach (var group in records
                         .Where(x => x.Type == wanted)
                         .GroupBy(x => new DateTimeOffset(x.Timestamp.Year, x.Timestamp.Month, x.Timestamp.Day,
                             x.Timestamp.Hour, x.Timestamp.Minute, 0, TimeSpan.Zero))
                         .OrderBy(x => x.Key))
                {
                    Console.WriteLine($"{group.Key:yyyy-MM-dd HH:mm}\t{group.Count()}");
                }

                return complete ? 0 : 1;
            }

            default:
                Console.Error.WriteLine($"query: unknown KIND '{kind}'");
                return 2;
        }
    }

    public static async Task<int> AdminAsync(string[] args)
    {
        try
        {
            return await runAdminAsync(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"admin: {e.Message}");
            return 2;
        }
    }

    private static async Task<int> runAdminAsync(string[] args)
    {
        var action = args.FirstOrDefault(x => !x.StartsWith('-'));
        using var client = Connect(args);

        switch (action)
        {
            case "reset-metrics":
            {
                var deleted = await client.DeleteByQueryAsync("from NodeRecords", CancellationToken.None);
                Console.WriteLine($"deleted {deleted} NodeRecords document(s)");
                return 0;
            }

            case "drop-database":
            {
                await client.DropDatabaseAsync(CancellationToken.None);
                Console.WriteLine($"dropped database '{client.Database}'");
                return 0;
            }

            default:
                Console.Error.WriteLine("admin: expected 'reset-metrics' or 'drop-database'");
                return 2;
        }
    }

    // ------------------------------------------------------------------ reads

    private static async Task<IReadOnlyList<(string Agent, Guid NodeId)>> assignmentsAsync(RavenClient client,
        CancellationToken token)
    {
        var rows = new List<(string, Guid)>();

        await foreach (var doc in pageAsync(client, "from AgentAssignments", token))
        {
            if (!doc.TryGetProperty("AgentUri", out var uri) || uri.GetString() is not { } agent) continue;
            if (!doc.TryGetProperty("NodeId", out var raw) || !Guid.TryParse(raw.GetString(), out var node)) continue;
            rows.Add((agent, node));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<(Guid Id, int Number, string Description)>> nodesAsync(RavenClient client,
        CancellationToken token)
    {
        var rows = new List<(Guid, int, string)>();

        await foreach (var doc in pageAsync(client, "from WolverineNodes", token))
        {
            if (!doc.TryGetProperty("NodeId", out var raw) || !Guid.TryParse(raw.GetString(), out var id)) continue;

            rows.Add((id,
                doc.TryGetProperty("AssignedNodeNumber", out var n) && n.TryGetInt32(out var number) ? number : 0,
                doc.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : ""));
        }

        return rows.OrderBy(x => x.Item2).ToList();
    }

    private static async Task<Dictionary<Guid, string>> podsByNodeAsync(RavenClient client, CancellationToken token)
        => (await nodesAsync(client, token)).ToDictionary(x => x.Id, x => x.Description);

    private static async Task<(List<(string Type, DateTimeOffset Timestamp)> Records, bool Complete)>
        scanRecordsAsync(RavenClient client, CancellationToken token)
    {
        var records = new List<(string, DateTimeOffset)>();
        var complete = true;

        await foreach (var doc in pageAsync(client, "from NodeRecords select Timestamp, RecordType", token))
        {
            if (records.Count >= MaxScan)
            {
                complete = false;
                Console.Error.WriteLine($"query: stopped after {MaxScan} NodeRecords documents — " +
                                        "the counts below are a LOWER BOUND, not a total");
                break;
            }

            var type = doc.TryGetProperty("RecordType", out var t) ? t.GetString() ?? "?" : "?";
            var timestamp = doc.TryGetProperty("Timestamp", out var ts) && ts.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : default;

            records.Add((type, timestamp));
        }

        return (records, complete);
    }

    /// <summary>
    /// Walk a collection query in pages. The <c>limit skip, take</c> form makes this a plain
    /// collection scan with no auto-index behind it, so no page can come back stale.
    /// </summary>
    private static async IAsyncEnumerable<JsonElement> pageAsync(RavenClient client, string rql,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var skip = 0;

        while (true)
        {
            var result = await client.QueryAsync($"{rql} limit {skip}, {Page}", token);

            foreach (var row in result.Results)
            {
                yield return row;
            }

            if (result.Results.Count < Page) yield break;
            skip += result.Results.Count;
        }
    }

    private static string? StringArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
