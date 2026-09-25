using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyLab.Cluster;

/// <summary>
/// Pod log text to history records: SIM-IDENTITY, AGENT-START/STOP, and the
/// <c>Wolverine.Runtime.Agents.*</c> control-plane lines. Both log shapes ChurnSim can produce are
/// read, and they must yield the SAME records:
/// <list type="bullet">
///   <item>the JSON console (<c>SIM_JSON_LOGS=true</c>, every manifest's default): one object per
///   line, with <c>Timestamp</c>, <c>LogLevel</c>, <c>Category</c>, <c>Message</c>;</item>
///   <item>the default text console, for captures that predate it: ChurnSim's own lines are bare
///   text, and a Wolverine record is TWO lines — <c>info: Category[0]</c> then the indented
///   message — which is why the text path is a state machine.</item>
/// </list>
/// Before this was JSON-aware the identity regex ran against the raw JSON line, and its trailing
/// <c>(\S+)</c> swallowed <c>"State":{...</c> along with the timestamp, so every identity on a
/// JSON-logging cluster was dated at harvest time instead. Control-plane lines were simply not
/// recognised at all. Neither failed; both produced a plausible, thinner history.
///
/// Timestamps come from the message body where the sim wrote one, because that is the clock
/// reading that matters; the console record's own timestamp is the fallback, and the
/// <c>kubectl logs --timestamps</c> prefix the fallback for that.
/// </summary>
public static partial class LogHarvest
{
    [GeneratedRegex(@"AGENT-(START|STOP)\s+(\S+)\s+at\s+(\S+)")]
    private static partial Regex AgentLine();

    [GeneratedRegex(@"SIM-IDENTITY\s+nodeId=(\S+)\s+podName=(\S+)\s+podIp=(\S+)\s+at\s+(\S+)")]
    private static partial Regex IdentityLine();

    /// <summary>The `kubectl logs --timestamps` RFC3339 prefix, if the follower asked for one.</summary>
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\S+?)\s+(.*)$")]
    private static partial Regex TimestampPrefix();

    /// <summary>
    /// The category line of .NET's two-line console logger, e.g.
    /// <c>info: Wolverine.Runtime.Agents.NodeAgentController[0]</c>. The message is on the NEXT
    /// line, indented.
    /// </summary>
    [GeneratedRegex(@"^(info|warn|fail|dbug|trce|crit)\s*:\s*(Wolverine\.Runtime\.Agents\.[^\[]+|Wolverine\.Runtime\.WolverineRuntime)\[")]
    private static partial Regex ControlCategoryLine();

    /// <summary>The same categories, as the JSON console names them.</summary>
    private static bool IsControlCategory(string category)
        => category.StartsWith("Wolverine.Runtime.Agents.", StringComparison.Ordinal) ||
           category == "Wolverine.Runtime.WolverineRuntime";

    /// <summary>
    /// The JSON console spells levels out; the text console abbreviates them. Records carry the
    /// text spelling, so a run harvested from either shape checks the same way.
    /// </summary>
    private static string ShortLevel(string level) => level switch
    {
        "Information" => "info",
        "Warning" => "warn",
        "Error" => "fail",
        "Debug" => "dbug",
        "Trace" => "trce",
        "Critical" => "crit",
        _ => level
    };

    public static IEnumerable<object> Parse(string pod, IEnumerable<string> lines)
    {
        // Set when the previous line was a text-console category header and we are waiting for
        // its indented message line.
        (DateTimeOffset Ts, string Level, string Category)? pendingControl = null;

        foreach (var raw in lines)
        {
            var line = raw;
            DateTimeOffset? lineTs = null;
            var prefix = TimestampPrefix().Match(raw);
            if (prefix.Success)
            {
                lineTs = ParseTs(prefix.Groups[1].Value, null);
                line = prefix.Groups[2].Value;
            }

            if (line.StartsWith('{'))
            {
                pendingControl = null;
                foreach (var record in ParseJson(pod, line, lineTs)) yield return record;
                continue;
            }

            if (pendingControl is { } pending)
            {
                pendingControl = null;
                var message = line.Trim();
                if (message.Length > 0)
                {
                    yield return new ControlEventRecord(pending.Ts, pod, pending.Level, pending.Category, message);
                }
            }

            var control = ControlCategoryLine().Match(line);
            if (control.Success)
            {
                pendingControl = (lineTs ?? DateTimeOffset.UtcNow, control.Groups[1].Value,
                    control.Groups[2].Value.Trim());
                continue;
            }

            foreach (var record in ParseMessage(pod, line, lineTs)) yield return record;
        }
    }

    private static IEnumerable<object> ParseJson(string pod, string line, DateTimeOffset? lineTs)
    {
        string? message, category, level;
        DateTimeOffset? ts;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            message = root.TryGetProperty("Message", out var m) ? m.GetString() : null;
            category = root.TryGetProperty("Category", out var c) ? c.GetString() : null;
            level = root.TryGetProperty("LogLevel", out var l) ? l.GetString() : null;
            ts = root.TryGetProperty("Timestamp", out var t) &&
                 DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : lineTs;
        }
        catch (JsonException)
        {
            // A truncated final line is ordinary at the tail of a live capture; it holds no record.
            yield break;
        }

        if (string.IsNullOrEmpty(message)) yield break;

        if (category is not null && IsControlCategory(category))
        {
            yield return new ControlEventRecord(ts ?? DateTimeOffset.UtcNow, pod, ShortLevel(level ?? ""),
                category, message);
        }

        foreach (var record in ParseMessage(pod, message, ts)) yield return record;
    }

    /// <summary>ChurnSim's own lines, which read the same whichever console wrote them.</summary>
    private static IEnumerable<object> ParseMessage(string pod, string message, DateTimeOffset? fallback)
    {
        var identity = IdentityLine().Match(message);
        if (identity.Success && Guid.TryParse(identity.Groups[1].Value, out var nodeId))
        {
            yield return new IdentityRecord(ParseTs(identity.Groups[4].Value, fallback), nodeId,
                identity.Groups[2].Value, identity.Groups[3].Value);
            yield break;
        }

        var agent = AgentLine().Match(message);
        if (agent.Success)
        {
            yield return new AgentEventRecord(ParseTs(agent.Groups[3].Value, fallback),
                agent.Groups[1].Value == "START" ? "start" : "stop", agent.Groups[2].Value, pod);
        }
    }

    private static DateTimeOffset ParseTs(string raw, DateTimeOffset? fallback)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : fallback ?? DateTimeOffset.UtcNow;
}
