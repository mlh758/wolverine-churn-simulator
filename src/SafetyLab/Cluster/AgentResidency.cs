using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyLab.Cluster;

/// <summary>
/// What a pod's log says is running on it right now, replayed from AGENT-START / AGENT-STOP.
///
/// The measurement that has proved most robust in this rig: it needs nothing but
/// <c>kubectl logs</c>, so it still works when the streaming capture, the checker, or the JSON
/// plumbing is itself under suspicion.
/// </summary>
public static partial class AgentResidency
{
    /// <summary>
    /// One pod's replay. <paramref name="Events"/> is the sentinel: a pod that produced no agent
    /// event at all was not observed, whatever its log length says, and "not observed" must never
    /// be reported as "running nothing".
    /// </summary>
    public sealed record Residency(
        string Pod,
        IReadOnlyList<string> Running,
        int Events,
        int Starts,
        int Stops,
        int Lines);

    /// <summary>
    /// One AGENT-START or AGENT-STOP. <paramref name="Timestamp"/> is null when the line carried
    /// none — <see cref="Replay"/> does not need it, but <see cref="Overlaps"/> cannot place an
    /// interval without one and drops those events rather than inventing a clock reading.
    /// </summary>
    public readonly record struct AgentEvent(DateTimeOffset? Timestamp, bool Start, string AgentUri);

    // The text format, for captures taken before SIM_JSON_LOGS existed. `\S+` is safe here only
    // because the line is known NOT to be JSON by the time these run — inside a JSON document the
    // same pattern happily captures `sim://agent1/",` with the punctuation attached.
    [GeneratedRegex(@"AGENT-START (\S+)")]
    private static partial Regex StartLine();

    [GeneratedRegex(@"AGENT-STOP (\S+)")]
    private static partial Regex StopLine();

    /// <summary>The `at &lt;iso8601&gt;` the sim writes into the message itself.</summary>
    [GeneratedRegex(@" at (\S+)")]
    private static partial Regex EmbeddedTimestamp();

    /// <summary>
    /// The `kubectl logs --timestamps` prefix, stripped first: a prefixed JSON record no longer
    /// starts with `{` and would fall through to the text path, which then captures a URI with
    /// punctuation glued on.
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\S+\s+(.*)$")]
    private static partial Regex TimestampPrefix();

    public static Residency Replay(string pod, IEnumerable<string> lines)
    {
        var running = new HashSet<string>(StringComparer.Ordinal);
        int starts = 0, stops = 0, count = 0;

        foreach (var raw in lines)
        {
            count++;

            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (!TryParseEvent(line, out var e)) continue;

            if (e.Start)
            {
                starts++;
                running.Add(e.AgentUri);
            }
            else
            {
                stops++;
                running.Remove(e.AgentUri);
            }
        }

        var ordered = running.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new Residency(pod, ordered, starts + stops, starts, stops, count);
    }

    /// <summary>
    /// One log line to an agent event, or false. The structured form is preferred — with
    /// SIM_JSON_LOGS=true <c>AgentUri</c> is a named field rather than something to match out of
    /// prose — and the text format is the fallback for captures that predate it.
    /// </summary>
    public static bool TryParseEvent(string raw, out AgentEvent agentEvent)
    {
        agentEvent = default;

        var line = raw.Trim();
        if (line.Length == 0) return false;

        var prefix = TimestampPrefix().Match(line);
        if (prefix.Success) line = prefix.Groups[1].Value.Trim();

        if (line.StartsWith('{'))
        {
            string? message;
            string? agentUri;
            DateTimeOffset? ts = null;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                message = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
                agentUri = root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Object &&
                           s.TryGetProperty("AgentUri", out var a)
                    ? a.GetString()
                    : null;

                if (root.TryGetProperty("Timestamp", out var t) &&
                    DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    ts = parsed;
                }
            }
            catch (JsonException)
            {
                // A truncated final line is ordinary at the tail of a live capture. It is not an
                // agent event, and it is counted as a line but not as an event.
                return false;
            }

            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(agentUri)) return false;

            if (message.Contains("AGENT-START", StringComparison.Ordinal))
            {
                agentEvent = new AgentEvent(ts, true, agentUri);
                return true;
            }

            if (message.Contains("AGENT-STOP", StringComparison.Ordinal))
            {
                agentEvent = new AgentEvent(ts, false, agentUri);
                return true;
            }

            return false;
        }

        var startMatch = StartLine().Match(line);
        var stopMatch = startMatch.Success ? Match.Empty : StopLine().Match(line);
        if (!startMatch.Success && !stopMatch.Success) return false;

        var embedded = EmbeddedTimestamp().Match(line);
        DateTimeOffset? textTs = embedded.Success &&
                                 DateTimeOffset.TryParse(embedded.Groups[1].Value, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v)
            ? v
            : null;

        agentEvent = startMatch.Success
            ? new AgentEvent(textTs, true, startMatch.Groups[1].Value)
            : new AgentEvent(textTs, false, stopMatch.Groups[1].Value);

        return true;
    }
}
