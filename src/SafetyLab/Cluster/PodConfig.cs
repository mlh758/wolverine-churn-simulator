using System.Globalization;
using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>
/// What a pod says its own configuration is, read from the <c>CONFIG</c> lines it writes at
/// startup.
///
/// docs/harness-traps.md: <c>kubectl set env</c> is silently undone by <c>kubectl apply</c>'s
/// three-way merge, which mislabelled a whole experiment arm — a run reported as "stock default"
/// actually had the settle gate on. The rule it produced is "verify configuration from the pod's
/// own output", and this is that, done as a refusal rather than a warning.
///
/// The structured form matters here. ChurnSim writes <c>State.Setting</c> and <c>State.Value</c>,
/// and <c>Value</c> is NULL when the knob does not exist on the Wolverine build under test — a
/// distinct outcome from "set to something else" and one that grepping the message prose cannot
/// see, because both lines start with the same word.
/// </summary>
public static class PodConfig
{
    private const string NotAvailable = "not available on this Wolverine build";

    /// <summary>
    /// Every CONFIG setting the pod reported. A present key with a null value is a knob the build
    /// does not have; an absent key was never reported at all.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Parse(IEnumerable<string> logLines)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var raw in logLines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // The text form. ChurnSim's startup emitter runs BEFORE a logger exists, so without
            // SIM_JSON_LOGS=true these lines are bare text -- which is the shape on any cluster
            // that has not enabled JSON logging, and the shape a JSON-only parser saw as "this pod
            // reported no configuration at all". Only the two spellings trySet() actually emits are
            // accepted, so the free-text CONFIG banners cannot invent a setting.
            if (!line.StartsWith('{'))
            {
                if (!line.StartsWith("CONFIG ", StringComparison.Ordinal)) continue;

                var rest = line["CONFIG ".Length..].Trim();

                if (rest.EndsWith(NotAvailable, StringComparison.Ordinal))
                {
                    var unavailable = rest[..^NotAvailable.Length].Trim();
                    if (unavailable.Length > 0 && !unavailable.Contains(' ')) settings[unavailable] = null;
                    continue;
                }

                var equals = rest.IndexOf('=');
                if (equals <= 0) continue;

                var key = rest[..equals];
                var reported = rest[(equals + 1)..];

                // Both halves must be space-free. trySet() only ever reports ints, bools and
                // TimeSpans, so a space means this is one of the free-text CONFIG banners --
                // `CONFIG backend=ravendb urls=… database=churnsim` would otherwise register a
                // setting called "backend" whose value is the rest of the sentence.
                if (key.Length == 0 || key.Contains(' ') || reported.Contains(' ')) continue;

                settings[key] = reported;
                continue;
            }

            if (!line.Contains("\"CONFIG", StringComparison.Ordinal)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Message", out var message) ||
                    message.GetString() is not { } text ||
                    !text.StartsWith("CONFIG", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object) continue;
                if (!state.TryGetProperty("Setting", out var setting) ||
                    setting.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                settings[name] = state.TryGetProperty("Value", out var value) && value.ValueKind != JsonValueKind.Null
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                // A truncated line at the tail of a live capture is not a CONFIG line.
            }
        }

        return settings;
    }

    /// <summary>
    /// Check reported settings against what the run is supposed to be measuring. Returns one
    /// problem line per disagreement — a caller that finds any must refuse, not warn: an arm whose
    /// configuration was never confirmed produces numbers filed under the wrong conditions, and
    /// nothing downstream says so.
    /// </summary>
    public static IReadOnlyList<string> Verify(
        IReadOnlyDictionary<string, string?> reported,
        IReadOnlyDictionary<string, string> expected)
    {
        var problems = new List<string>();

        foreach (var (key, want) in expected.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!reported.TryGetValue(key, out var got))
            {
                problems.Add($"{key}: expected '{want}', but the pod never reported it at all");
                continue;
            }

            if (got is null)
            {
                problems.Add(
                    $"{key}: expected '{want}', but this Wolverine build does not have that knob " +
                    "(the pod reported it as unavailable)");
                continue;
            }

            // Wolverine reports TimeSpan-valued knobs as 00:00:04 while the env var says 4, so an
            // exact string match would reject a correctly configured pod. Compare as durations when
            // both sides can be read as one, and fall back to text.
            if (!Matches(got, want))
            {
                problems.Add($"{key}: expected '{want}', pod reports '{got}'");
            }
        }

        return problems;
    }

    private static bool Matches(string got, string want)
    {
        if (string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) return true;

        return TryDuration(got, out var a) && TryDuration(want, out var b) && a == b;
    }

    private static bool TryDuration(string value, out TimeSpan span)
    {
        // A bare number is SECONDS -- every SIM_*_SECONDS env var in this repo is written that way.
        //
        // This has to be tried BEFORE TimeSpan.TryParse, which accepts "4" and reads it as four
        // DAYS. Left in the other order, verifying StaleNodeTimeout=4 against a pod correctly
        // reporting 00:00:04 compared 4.00:00:00 to 00:00:04, failed, and would have refused to
        // run on every properly configured cluster.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            span = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out span);
    }
}
