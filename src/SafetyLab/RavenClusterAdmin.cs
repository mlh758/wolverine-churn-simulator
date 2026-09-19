namespace SafetyLab;

/// <summary>
/// Forming and describing a replicated RavenDB store, for the partition arm (E7).
///
/// A RavenDB server started with <c>Setup.Mode=None</c> is <em>passive</em>: it answers
/// <c>/build/version</c> and nothing else until it is either bootstrapped into a one-node cluster
/// or added to an existing one. Three such servers are three passive singletons, not a cluster,
/// and a database created on one of them is replicated to nobody. So the order on this arm is
/// fixed and this verb enforces it: bootstrap the first member, add the rest, wait for them to be
/// members, then create the database at the wanted replication factor — all BEFORE the first
/// ChurnSim pod starts, because ChurnSim creates the database with factor 1 when it finds none,
/// and a factor-1 database on a three-member cluster is exactly the "cluster" that replicates
/// nothing and would let every partition finding be filed against a store that was never
/// replicated.
///
/// Idempotent throughout: every step checks the server's state and skips what is already done,
/// so it can run after a pod restart or twice in a row without complaint.
/// </summary>
public static class RavenClusterAdmin
{
    public static async Task<int> FormAsync(string? urls, string? database, int replicationFactor)
    {
        try
        {
            return await formAsync(urls, database, replicationFactor);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"raven-cluster form: {e.Message}");

            // Measured 2026-09-18 on 7.0.9: an unlicensed server bootstraps happily and then
            // answers 402 LicenseLimitException to the first add. Say what to do, because the
            // server's message says only what it will not do.
            if (e.Message.Contains("LicenseLimit", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("license", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("raven-cluster form: an unlicensed RavenDB runs ONE node and refuses to add " +
                                        "members. Register the free Developer license (three nodes) at " +
                                        "https://ravendb.net/license/request, store it with `just ravendb-license " +
                                        "<file>`, restart the ravendb pods so they read it, and re-run.");
            }

            return 2;
        }
    }

    private static async Task<int> formAsync(string? urls, string? database, int replicationFactor)
    {
        var clients = RavenQueries.ConnectAll(urls, database);
        var token = CancellationToken.None;

        if (clients.Count < replicationFactor)
        {
            Console.Error.WriteLine($"raven-cluster form: replication factor {replicationFactor} needs at least " +
                                    $"{replicationFactor} urls, got {clients.Count}");
            return 2;
        }

        // Every member has to be answering before anything is decided about it. A StatefulSet
        // brings its pods up in order and the last one can be a minute behind the first.
        foreach (var client in clients)
        {
            await waitAsync($"{client.Url} to answer", TimeSpan.FromMinutes(3), async () =>
            {
                var version = await client.BuildVersionAsync(token);
                return !version.StartsWith("unavailable", StringComparison.Ordinal);
            });
        }

        var first = clients[0];
        var view = await first.ClusterTopologyAsync(token);

        if (view.State is null or "Passive" || view.Members.Count == 0)
        {
            Console.Error.WriteLine($"  bootstrapping {first.Url} (was {view.State ?? "unknown"})");
            await first.BootstrapAsync(token);
        }

        await waitAsync($"{first.Url} to lead a cluster", TimeSpan.FromMinutes(1), async () =>
        {
            var v = await first.ClusterTopologyAsync(token);
            return v.Leader is not null && v.Members.Count > 0;
        });

        view = await first.ClusterTopologyAsync(token);
        Console.Error.WriteLine($"  {first.Url} is {view.NodeTag} ({view.State}), leader {view.Leader}");

        // Add every other url that is not already a member, by url. Tags are handed out in order
        // after whatever the cluster already has, so a re-run does not collide.
        var used = view.Members.Keys.ToHashSet(StringComparer.Ordinal);
        var nextTag = 'A';

        foreach (var client in clients.Skip(1))
        {
            if (view.Members.Values.Any(m => sameUrl(m, client.Url)))
            {
                Console.Error.WriteLine($"  {client.Url} is already a member");
                continue;
            }

            while (used.Contains(nextTag.ToString())) nextTag++;
            var tag = nextTag.ToString();
            used.Add(tag);

            Console.Error.WriteLine($"  adding {client.Url} as {tag}");
            await first.AddNodeAsync(client.Url, tag, token);

            await waitAsync($"{client.Url} to become a member", TimeSpan.FromMinutes(2), async () =>
            {
                var v = await first.ClusterTopologyAsync(token);
                return v.Members.Values.Any(m => sameUrl(m, client.Url));
            });

            view = await first.ClusterTopologyAsync(token);
        }

        // Every member must agree it is in the cluster and see the same leader before a database
        // is created across them; otherwise the create can land on a member that is still joining.
        foreach (var client in clients)
        {
            await waitAsync($"{client.Url} to see the leader", TimeSpan.FromMinutes(1), async () =>
            {
                var v = await client.ClusterTopologyAsync(token);
                return v.Leader is not null && v.Members.Count >= clients.Count;
            });
        }

        if (!await first.DatabaseExistsAsync(token))
        {
            Console.Error.WriteLine($"  creating database '{first.Database}' at replication factor {replicationFactor}");
            await first.CreateDatabaseAsync(replicationFactor, token);
        }
        else
        {
            Console.Error.WriteLine($"  database '{first.Database}' already exists — its replication factor is " +
                                    "whatever it was created with; check the topology below");
        }

        // The database is only "there" when every member of its group serves it.
        await waitAsync($"database '{first.Database}' to be served by {replicationFactor} member(s)",
            TimeSpan.FromMinutes(2), async () =>
            {
                var topology = await first.DatabaseTopologyAsync(token);
                if (topology.Count(x => x.Role == "Member") < replicationFactor) return false;

                foreach (var client in clients)
                {
                    try
                    {
                        await client.ConflictCountAsync(token);
                    }
                    catch (HttpRequestException)
                    {
                        return false;
                    }
                }

                return true;
            });

        return await StatusAsync(urls, database);
    }

    /// <summary>
    /// Exit 0 only when every member answers, every member sees a leader and they all name the
    /// SAME one, and the database is served by as many members as there are urls. Anything less
    /// is exit 1: the partition script gates on this, because a cut applied to a cluster that
    /// was already disagreeing with itself measures nothing.
    /// </summary>
    public static async Task<int> StatusAsync(string? urls, string? database)
    {
        try
        {
            var clients = RavenQueries.ConnectAll(urls, database);
            var token = CancellationToken.None;
            var healthy = true;
            var leaders = new HashSet<string>(StringComparer.Ordinal);

            Console.WriteLine("member\ttag\tstate\tleader\tmembers-seen");
            foreach (var client in clients)
            {
                try
                {
                    var v = await client.ClusterTopologyAsync(token);
                    Console.WriteLine($"{RunHistory.ShortNode(client.Url)}\t{v.NodeTag ?? "-"}\t{v.State ?? "-"}\t" +
                                      $"{v.Leader ?? "none"}\t{v.Members.Count}");

                    if (v.Leader is null || v.Members.Count < clients.Count) healthy = false;
                    else leaders.Add(v.Leader);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{RunHistory.ShortNode(client.Url)}\t-\tunreachable\t-\t-\t{e.Message}");
                    healthy = false;
                }
            }

            if (leaders.Count > 1) healthy = false;

            Console.WriteLine();
            Console.WriteLine($"database '{clients[0].Database}' topology (from {RunHistory.ShortNode(clients[0].Url)}):");
            var members = 0;
            foreach (var (tag, url, role) in await clients[0].DatabaseTopologyAsync(token))
            {
                Console.WriteLine($"  {tag}\t{role}\t{url}");
                if (role == "Member") members++;
            }

            if (members < clients.Count)
            {
                Console.WriteLine($"  ({members} member(s) serve the database; {clients.Count} expected)");
                healthy = false;
            }

            if (!healthy)
            {
                Console.Error.WriteLine("raven-cluster status: NOT HEALTHY — a member is unreachable, leaderless, " +
                                        "disagrees about the leader, or does not serve the database");
                return 1;
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"raven-cluster status: {e.Message}");
            return 2;
        }
    }

    /// <summary>
    /// The member a DEFAULT RavenDB client talks to: the first node in the database group's
    /// topology. With <c>ReadBalanceBehavior.None</c> (the default) every client prefers that
    /// node and only fails over when it cannot be reached, so on the un-pinned arm every ChurnSim
    /// pod is on it regardless of the url it was given. The store-tier partition (run 2) has to
    /// isolate this member, or it isolates one nobody is using and measures nothing.
    /// Prints the short name (e.g. <c>ravendb-1</c>) and exits 0, or exits 2 if the topology cannot
    /// be read.
    /// </summary>
    public static async Task<int> PreferredAsync(string? urls, string? database)
    {
        try
        {
            var clients = RavenQueries.ConnectAll(urls, database);
            var topology = await clients[0].DatabaseTopologyAsync(CancellationToken.None);
            var first = topology.FirstOrDefault(x => x.Role == "Member");

            if (first.Url is null)
            {
                Console.Error.WriteLine("raven-cluster preferred: the database topology has no Member");
                return 2;
            }

            Console.WriteLine(RunHistory.ShortNode(first.Url));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"raven-cluster preferred: {e.Message}");
            return 2;
        }
    }

    private static bool sameUrl(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static async Task waitAsync(string what, TimeSpan timeout, Func<Task<bool>> ready)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await ready()) return;
            }
            catch (Exception e)
            {
                last = e;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"waited {timeout.TotalSeconds:F0}s for {what}" +
                                   (last is null ? "" : $"; last error: {last.Message}"));
    }
}
