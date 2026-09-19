using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>One pod, as much of it as any decision here needs.</summary>
/// <param name="Ip">
/// <c>status.podIP</c>, or empty before the network is attached. The partition injector keys its
/// firewall rules on this, and refuses a pod without one rather than writing a rule that matches
/// nothing.
/// </param>
public sealed record PodInfo(string Name, string Phase, bool Ready, bool Terminating, int RestartCount, string Ip = "")
{
    /// <summary>
    /// Live means Running, Ready, and NOT terminating — all three, because each one alone has
    /// already produced a wrong measurement in this rig.
    ///
    /// A terminating pod keeps <c>phase: Running</c> for its whole terminationGracePeriodSeconds,
    /// so straight after a rollout the "Running" set still contains the previous ReplicaSet's pods,
    /// carrying the previous iteration's environment and agents. A pod that is Running but not
    /// Ready is mid-startup and has no agents yet. And a Deployment that was just restarted leaves
    /// its predecessor behind in Failed/Succeeded, which is what made `kubectl exec` report
    /// "cannot exec into a container in a completed pod" in the middle of a measurement.
    /// </summary>
    public bool IsLive => Phase == "Running" && Ready && !Terminating;
}

/// <summary>
/// Parsing and selection over <c>kubectl get pods -o json</c>. Pure: it takes the JSON text and
/// returns records, so every rule above is testable against a captured fixture with no cluster.
///
/// This exists because selecting <c>.items[0]</c> in a jsonpath expression is not a selection, it
/// is a coin flip that happens to be right most of the time.
/// </summary>
public static class PodSelection
{
    public static IReadOnlyList<PodInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pods = new List<PodInfo>();

        foreach (var item in items.EnumerateArray())
        {
            var metadata = item.TryGetProperty("metadata", out var m) ? m : default;
            var status = item.TryGetProperty("status", out var s) ? s : default;

            var name = metadata.ValueKind == JsonValueKind.Object &&
                       metadata.TryGetProperty("name", out var n)
                ? n.GetString() ?? ""
                : "";

            if (name.Length == 0) continue;

            var phase = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var p)
                ? p.GetString() ?? ""
                : "";

            var terminating = metadata.ValueKind == JsonValueKind.Object &&
                              metadata.TryGetProperty("deletionTimestamp", out var d) &&
                              d.ValueKind is not JsonValueKind.Null;

            var ready = false;
            var restarts = 0;

            if (status.ValueKind == JsonValueKind.Object &&
                status.TryGetProperty("containerStatuses", out var containers) &&
                containers.ValueKind == JsonValueKind.Array)
            {
                // A pod is ready when every container in it is. `containerStatuses[0]` is the same
                // class of mistake as `items[0]`; it happens to be right for a single-container pod
                // and quietly wrong for any other.
                var all = containers.EnumerateArray().ToArray();
                ready = all.Length > 0 && all.All(c => c.TryGetProperty("ready", out var r) && r.GetBoolean());
                restarts = all.Sum(c => c.TryGetProperty("restartCount", out var rc) && rc.TryGetInt32(out var v)
                    ? v
                    : 0);
            }

            var ip = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("podIP", out var addr)
                ? addr.GetString() ?? ""
                : "";

            pods.Add(new PodInfo(name, phase, ready, terminating, restarts, ip));
        }

        return pods;
    }

    public static IReadOnlyList<PodInfo> Live(string json)
        => Parse(json).Where(x => x.IsLive).ToList();

    /// <summary>
    /// Exactly one live pod, or an explanation. Callers that need a single pod to act on — exec
    /// into the monitor, kill the leader — must not silently take the first of several or the
    /// first of none.
    /// </summary>
    public static bool TrySelectSingle(string json, out PodInfo? pod, out string problem)
    {
        var live = Live(json);
        pod = null;
        problem = "";

        switch (live.Count)
        {
            case 0:
                var parsed = Parse(json);
                problem = parsed.Count == 0
                    ? "no pods matched at all"
                    : $"no LIVE pod among {parsed.Count}: " +
                      string.Join(", ", parsed.Select(x =>
                          $"{x.Name} (phase={x.Phase}, ready={x.Ready}, terminating={x.Terminating})"));
                return false;

            default:
                pod = live[0];
                return true;
        }
    }
}
