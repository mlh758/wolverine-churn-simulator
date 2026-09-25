using System.Globalization;
using System.Text.RegularExpressions;

namespace SafetyLab.Cluster;

/// <summary>
/// The CRI log line format, which is what the kubelet writes to <c>/var/log/pods</c> and what the
/// node-side tailer (k8s/logtail.yaml) copies verbatim:
/// <code>2026-09-25T18:13:25.660195389Z stdout F SIM-IDENTITY nodeId=...</code>
/// A timestamp, the stream, a tag, and the application's line. The tag is <c>F</c> for a full line
/// and <c>P</c> for a partial one — the runtime splits a line longer than its buffer (16 KB) into
/// several <c>P</c> records and a closing <c>F</c>, so a decoder that treats every record as a
/// line would hand a JSON reader a fragment and lose the whole record.
///
/// Decoding lives here rather than in a shell pipeline so the join is a tested decision
/// (docs/harness-traps.md, rule 3), and the output is exactly what <c>kubectl logs</c> would have
/// printed: every existing consumer — AgentResidency, PodConfig, LogHarvest, scripts/logq.sh —
/// reads it unchanged.
/// </summary>
public static partial class CriLog
{
    /// <summary>One application line, as the container wrote it, with the node's clock reading.</summary>
    public readonly record struct Entry(DateTimeOffset Ts, string Stream, string Text);

    /// <summary>
    /// <paramref name="Records"/> is CRI records read; <paramref name="Entries"/> is application
    /// lines produced, which is fewer whenever partials were joined. <paramref name="Malformed"/>
    /// counts records that did not fit the format and were dropped — on a file the tailer copied
    /// that number must be zero, and a caller that sees otherwise should say so rather than
    /// report a slightly shorter log.
    /// </summary>
    public sealed record Decoded(IReadOnlyList<Entry> Entries, int Records, int Joined, int Malformed)
    {
        public DateTimeOffset? First => Entries.Count == 0 ? null : Entries[0].Ts;
        public DateTimeOffset? Last => Entries.Count == 0 ? null : Entries[^1].Ts;
    }

    // `(.*)` rather than `(.+)`: an empty application line is `<ts> stdout F ` with nothing after
    // the tag, and the trailing space is not always there.
    [GeneratedRegex(@"^(\S+) (stdout|stderr) ([PF]) ?(.*)$", RegexOptions.Singleline)]
    private static partial Regex Record();

    public static Decoded Decode(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        int records = 0, joined = 0, malformed = 0;

        // Partials are joined per stream: stdout and stderr are separate pipes and their records
        // can interleave, so a pending stdout fragment must not swallow a stderr line.
        var pending = new Dictionary<string, (DateTimeOffset Ts, System.Text.StringBuilder Text)>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            if (raw.Length == 0) continue;
            records++;

            var match = Record().Match(raw);
            if (!match.Success ||
                !DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
            {
                malformed++;
                continue;
            }

            var stream = match.Groups[2].Value;
            var full = match.Groups[3].Value == "F";
            var text = match.Groups[4].Value;

            if (pending.TryGetValue(stream, out var open))
            {
                open.Text.Append(text);
                joined++;
                if (!full) continue;

                pending.Remove(stream);
                entries.Add(new Entry(open.Ts, stream, open.Text.ToString()));
                continue;
            }

            if (full)
            {
                entries.Add(new Entry(ts, stream, text));
                continue;
            }

            pending[stream] = (ts, new System.Text.StringBuilder(text));
        }

        // A fragment with no closing F is a line the container was killed in the middle of. It is
        // still what the process wrote, so it is kept — as its own line, in clock order with the
        // rest, rather than dropped for being unterminated.
        foreach (var (stream, open) in pending)
        {
            entries.Add(new Entry(open.Ts, stream, open.Text.ToString()));
        }

        if (pending.Count > 0)
        {
            entries.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        }

        return new Decoded(entries, records, joined, malformed);
    }

    /// <summary>
    /// The tailer names its files <c>&lt;pod&gt;.&lt;attempt&gt;.cri</c>, the attempt being the
    /// container's restart ordinal on the node: the victim of a SIGKILL is attempt 0, what came
    /// back is attempt 1. Anything else in the directory (the tailer's own offset database, a
    /// stray file) is not a log and is skipped by the caller.
    /// </summary>
    [GeneratedRegex(@"^(?<pod>[a-z0-9]([-a-z0-9]*[a-z0-9])?)\.(?<attempt>\d+)\.cri$")]
    private static partial Regex FileName();

    public static bool TryParseFileName(string name, out string pod, out int attempt)
    {
        pod = "";
        attempt = 0;

        var match = FileName().Match(name);
        if (!match.Success) return false;

        pod = match.Groups["pod"].Value;
        attempt = int.Parse(match.Groups["attempt"].Value, CultureInfo.InvariantCulture);
        return true;
    }
}
