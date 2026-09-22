using System.Text.Json;

namespace SafetyLab;

/// <summary>
/// One holder of the leadership lock, normalised across backends.
/// <paramref name="Where"/> is the server-side evidence in the backend's own vocabulary (a
/// Postgres pid or MySQL connection id with its client address, or a RavenDB compare-exchange key
/// and Raft index) and goes straight into violation text, so a report still says where to go and
/// look.
/// <paramref name="ExpiresAt"/> is RavenDB-only and null on both RDBMS arms, where a lock has no
/// expiry because the death of its session <em>is</em> its release.
/// </summary>
public sealed record LeaderHolder(string Where, Guid? NodeId, DateTimeOffset? ExpiresAt);

/// <summary>
/// A captured run directory, loaded back for offline checking:
///
/// <list type="bullet">
///   <item><c>history.jsonl</c> — the monitor's samples (server-side truth)</item>
///   <item><c>pods.jsonl</c> — node identities and AGENT-START/STOP events harvested from pod logs</item>
///   <item><c>marks.jsonl</c> — phase markers written by the scripts</item>
/// </list>
///
/// The split matters: the monitor's stream is a single clock (its own) against a single
/// database, while the pod stream carries one clock per pod. Checks that mix the two are
/// grace-windowed for exactly that reason, and say so.
/// </summary>
public sealed class RunHistory
{
    public required string Directory { get; init; }
    public MetaRecord? Meta { get; init; }
    public required IReadOnlyList<Sample> Samples { get; init; }
    public required IReadOnlyList<IdentityRecord> Identities { get; init; }
    public required IReadOnlyList<AgentEventRecord> AgentEvents { get; init; }
    public required IReadOnlyList<MarkRecord> Marks { get; init; }

    // Uri.ToString() normalises an authority-only URI with a trailing slash, so what Wolverine
    // actually writes is "wolverine://leader/" -- and likewise "sim://agent1/". Match both forms:
    // the first live run was checked against the unslashed literal and every leader-side check
    // silently passed for want of a row to fail on.
    public const string LeaderUri = "wolverine://leader/";

    public static bool IsLeaderUri(string id)
        => id == LeaderUri || id == "wolverine://leader";

    /// <summary>
    /// Wolverine's PostgreSQL leadership lock is <c>schemaName.GetDeterministicHashCode()</c>
    /// (<c>PostgresqlNodePersistence._lockId</c>), NOT the <c>LeaderLockId = 9999999</c> constant
    /// that sits in the same class and is unused by the leadership path. Watching 9999999 finds
    /// nothing, and "nothing" reads as "no violation" on every leader check — which is exactly
    /// what happened on the first live run, and exactly what the S1 sentinel exists to catch.
    ///
    /// JasperFx.Core's deterministic string hash, reimplemented rather than referenced so the
    /// monitor keeps no Wolverine dependency.
    /// </summary>
    public static int LockIdForSchema(string schema)
    {
        unchecked
        {
            var hash1 = (5381 << 16) + 5381;
            var hash2 = hash1;

            for (var i = 0; i < schema.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ schema[i];
                if (i == schema.Length - 1) break;
                hash2 = ((hash2 << 5) + hash2) ^ schema[i + 1];
            }

            return hash1 + hash2 * 1566083941;
        }
    }

    public long LeaderLockId => Meta?.LeaderLockId ?? LockIdForSchema("wolverine");

    /// <summary>
    /// Which message store this run watched. A history with no <c>backend</c> in its meta record
    /// predates the RavenDB arm, and every one of those was PostgreSQL — so the default is not a
    /// guess.
    /// </summary>
    public string Backend => Meta?.BackendName ?? Backends.Postgres;

    public bool IsRavenDb => Backend == Backends.RavenDb;

    public bool IsMySql => Backend == Backends.MySql;

    /// <summary>
    /// Whether this run's leadership lock belongs to a SESSION — true on both RDBMS arms, false on
    /// RavenDB. Checkers branch on this rather than on the product name wherever the question is
    /// about the protocol (S8 has nothing to describe when a lock cannot outlive its holder);
    /// <see cref="IsRavenDb"/> stays for the places where the question really is "is this RavenDB".
    /// </summary>
    public bool IsSessionLock => Backends.IsSessionLock(Backend);

    /// <summary>The store's name as a reader would write it, for report prose.</summary>
    public string StoreName => Backend switch
    {
        Backends.MySql => "MySQL",
        Backends.RavenDb => "RavenDB",
        _ => "PostgreSQL"
    };

    /// <summary>
    /// What this store calls its session-scoped lock, and what it calls the thing that holds one.
    /// PostgreSQL: an advisory lock held by a backend. MySQL: a named lock held by a connection.
    /// Same mechanism, and a report that used one store's words for the other sends a reader to
    /// a catalog view that does not exist.
    /// </summary>
    public string SessionLockNoun => IsMySql ? "named lock" : "advisory lock";

    public string SessionHolderNoun => IsMySql ? "connection" : "backend";

    /// <summary>
    /// RavenDB's leadership compare-exchange key. Matches both the suffixed form the store settles
    /// on (<c>wolverine/leader/&lt;service&gt;</c>) and the un-suffixed one its constructor starts
    /// with, because a lock held under both at once is a genuine split and must be visible as two
    /// holders rather than filtered down to one.
    /// </summary>
    public static bool IsLeaderLockKey(string key)
        => key == "wolverine/leader" || key.StartsWith("wolverine/leader/", StringComparison.Ordinal);

    public IEnumerable<Sample> Good => Samples.Where(x => x.Error is null);

    public static RunHistory Load(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No such run directory: {directory}");
        }

        MetaRecord? meta = null;
        var samples = new List<Sample>();
        var identities = new List<IdentityRecord>();
        var agents = new List<AgentEventRecord>();
        var marks = new List<MarkRecord>();

        // One pods file per pod (pods.<podname>.jsonl). The collector follows each pod's log in
        // its own process, and giving them separate files removes any question about interleaved
        // appends -- at the cost of nothing, since the loader sorts everything by timestamp anyway.
        var paths = new List<string> { Path.Combine(directory, "history.jsonl"), Path.Combine(directory, "marks.jsonl") };
        paths.AddRange(System.IO.Directory.GetFiles(directory, "pods*.jsonl").OrderBy(x => x));

        foreach (var path in paths)
        {
            var file = Path.GetFileName(path);
            if (!File.Exists(path)) continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException e)
                {
                    // A run captured with `kubectl logs -f` can be cut mid-line when the pod
                    // goes away. Losing the tail of a file is survivable; silently losing it is
                    // not, so say which line went.
                    Console.Error.WriteLine($"warning: {file}:{lineNumber} is not valid JSON, skipped ({e.Message})");
                    continue;
                }

                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("kind", out var kindProperty)) continue;

                    var raw = doc.RootElement.GetRawText();
                    switch (kindProperty.GetString())
                    {
                        case "meta":
                            meta = JsonSerializer.Deserialize<MetaRecord>(raw, Json.Options);
                            break;
                        case "sample":
                            add(samples, JsonSerializer.Deserialize<Sample>(raw, Json.Options));
                            break;
                        case "identity":
                            add(identities, JsonSerializer.Deserialize<IdentityRecord>(raw, Json.Options));
                            break;
                        case "agent":
                            add(agents, JsonSerializer.Deserialize<AgentEventRecord>(raw, Json.Options));
                            break;
                        case "mark":
                            add(marks, JsonSerializer.Deserialize<MarkRecord>(raw, Json.Options));
                            break;
                    }
                }
            }
        }

        return new RunHistory
        {
            Directory = directory,
            Meta = meta,
            Samples = samples.OrderBy(x => x.Ts).ToList(),
            Identities = identities.OrderBy(x => x.Ts).ToList(),
            AgentEvents = agents.OrderBy(x => x.Ts).ToList(),
            Marks = marks.OrderBy(x => x.Ts).ToList()
        };

        static void add<T>(List<T> list, T? value)
        {
            if (value is not null) list.Add(value);
        }
    }

    /// <summary>
    /// Resolve a backend's <c>client_addr</c> to the Wolverine node id running in that pod.
    /// Pod IPs are recycled by Kubernetes, so the answer is time-dependent: take the most
    /// recent identity announcement for that IP at or before <paramref name="at"/>.
    /// </summary>
    public Guid? NodeForAddress(string? clientAddr, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(clientAddr)) return null;

        IdentityRecord? best = null;
        foreach (var identity in Identities)
        {
            if (identity.PodIp != clientAddr) continue;
            if (identity.Ts > at) break;
            best = identity;
        }

        return best?.NodeId;
    }

    public string? PodForNode(Guid nodeId)
        => Identities.LastOrDefault(x => x.NodeId == nodeId)?.PodName;

    /// <summary>
    /// The leader lock's granted holders in a sample. More than one would be a store bug on either
    /// RDBMS arm. <c>pg_locks.objid</c> is an unsigned <c>oid</c>, so a schema whose hash is
    /// negative appears as the 2^32 complement — compare on the low 32 bits rather than the signed
    /// value. (MySQL carries the id as the signed integer in the lock's name, so the mask is a
    /// no-op there; one comparison serves both.)
    /// </summary>
    public IEnumerable<LockRow> LeaderLockHolders(Sample sample)
    {
        var wanted = (uint)LeaderLockId;
        return sample.Locks.Where(x => (uint)x.ObjId == wanted && x.Granted);
    }

    /// <summary>
    /// Whoever holds leadership in this sample, in backend-neutral terms. Every leader-side
    /// checker goes through here, which is the whole reason the RavenDB arm did not need a second
    /// copy of them: the properties ("at most one holder", "the holder and the row agree", "the
    /// holder is still a registered node") are about the protocol, not about the store.
    ///
    /// The backends differ in what they can answer, and the difference is real rather than
    /// cosmetic. The RDBMS arms know only an address (<c>pg_stat_activity.client_addr</c>,
    /// <c>performance_schema.threads.PROCESSLIST_HOST</c>), so <see cref="LeaderHolder.NodeId"/>
    /// is null whenever the identity map cannot place it — a case S4 exists to report. On RavenDB
    /// the lock document names its owner, so the node id is always known and never needs pod logs;
    /// what RavenDB adds instead is <see cref="LeaderHolder.ExpiresAt"/>, which a session lock has
    /// no equivalent for at all.
    /// </summary>
    public IReadOnlyList<LeaderHolder> LeaderHolders(Sample sample)
    {
        if (IsRavenDb)
        {
            // On a replicated capture the same key is read from every member and each row is
            // tagged with its source. Members that AGREE (same key, same owner) are one holder —
            // a healthy three-member read is one leader, not three. Members that disagree are
            // separate holders, and the `Where` text names which member said what, because that
            // disagreement is precisely the partition finding S1 exists to surface here.
            return (sample.Cmpxchg ?? [])
                .Where(x => IsLeaderLockKey(x.Key))
                .GroupBy(x => (x.Key, x.NodeId))
                .Select(g =>
                {
                    var sources = g.Where(x => x.Node is not null).Select(x => ShortNode(x.Node!)).Distinct().ToList();
                    var seen = sources.Count > 0 ? $" seen on {string.Join("+", sources)}" : "";
                    var index = g.Max(x => x.Index);
                    return new LeaderHolder($"cmpxchg '{g.Key.Key}' at index {index}{seen}", g.Key.NodeId,
                        g.Max(x => x.ExpiresAt));
                })
                .ToList();
        }

        // "pid" on PostgreSQL, "connection" on MySQL -- the number means the same thing (it is
        // what you would terminate) but naming it wrong sends a reader to the wrong catalog view.
        var holderNoun = IsMySql ? "connection" : "pid";

        return LeaderLockHolders(sample)
            .Select(x => new LeaderHolder($"{holderNoun} {x.Pid} ({x.ClientAddr})",
                NodeForAddress(x.ClientAddr, sample.Ts), null))
            .ToList();
    }

    /// <summary>
    /// Was this capture read from more than one RavenDB member? Decides whether S9/S10/P1 have
    /// anything to say, and is false for every history written before the replicated arm.
    /// </summary>
    public bool IsReplicated => Meta?.Replicas is { Count: > 1 } || Samples.Any(x => x.Replicas is { Count: > 0 });

    /// <summary>The member urls the monitor read, primary first; empty on a single-node capture.</summary>
    public IReadOnlyList<string> ReplicaUrls
        => Meta?.Replicas ?? Samples.FirstOrDefault(x => x.Replicas is { Count: > 0 })?.Replicas!
            .Select(x => x.Url).ToList() ?? [];

    /// <summary>
    /// "ravendb-2" out of "http://ravendb-2.ravendb.default.svc.cluster.local:8080" — the host's
    /// first label, which on the StatefulSet is the pod name. Falls back to the whole url when
    /// it does not parse, so a report never shows an empty source.
    /// </summary>
    public static string ShortNode(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            var host = uri.Host;
            var dot = host.IndexOf('.');
            return dot > 0 ? host[..dot] : host;
        }

        return url;
    }

    /// <summary>How the leader lock is named in a report line, for the header of a run.</summary>
    public string LeaderLockDescription => Backend switch
    {
        Backends.RavenDb => $"compare-exchange '{Meta?.LockKey ?? "wolverine/leader/*"}'",
        // The id is carried too, not just the name: it is the only thing that ties the lock back
        // to the schema it was derived from, and a wrong one is the failure S1 exists to catch.
        Backends.MySql => $"named lock '{Meta?.LockKey ?? MySqlClusterMonitor.LockName(LeaderLockId)}' " +
                          $"(id {LeaderLockId})",
        _ => $"advisory lock {LeaderLockId}"
    };

    public static AssignmentRow? LeaderRow(Sample sample)
        => sample.Assignments.FirstOrDefault(x => IsLeaderUri(x.Id));

    public static IEnumerable<AssignmentRow> SimAssignments(Sample sample)
        => sample.Assignments.Where(x => x.Id.StartsWith("sim://", StringComparison.Ordinal));
}
