using System.Text.Json;

namespace SafetyLab;

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

    public const string LeaderUri = "wolverine://leader";
    public const long DefaultLeaderLockId = 9999999;

    public long LeaderLockId => Meta?.LeaderLockId ?? DefaultLeaderLockId;

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

    /// <summary>The leader lock's granted holders in a sample. More than one would be a Postgres bug.</summary>
    public IEnumerable<LockRow> LeaderLockHolders(Sample sample)
        => sample.Locks.Where(x => x.ObjId == LeaderLockId && x.Granted);

    public static AssignmentRow? LeaderRow(Sample sample)
        => sample.Assignments.FirstOrDefault(x => x.Id == LeaderUri);

    public static IEnumerable<AssignmentRow> SimAssignments(Sample sample)
        => sample.Assignments.Where(x => x.Id.StartsWith("sim://", StringComparison.Ordinal));
}
