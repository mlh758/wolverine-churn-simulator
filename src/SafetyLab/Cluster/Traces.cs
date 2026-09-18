using System.Globalization;
using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>
/// Wolverine's own spans, out of Jaeger, for the question state sampling cannot answer: when the
/// leader dispatches an assignment batch, how long until it is confirmed, and where does the
/// wall-clock actually go?
///
/// <c>wget</c> inside the already-deployed Jaeger pod: nothing on the host can reach a ClusterIP
/// service, and this rig does not open one.
/// </summary>
public static class Traces
{
    public sealed record Span(double Milliseconds, string Operation, string TraceId);

    public sealed record Summary(IReadOnlyList<Span> Spans)
    {
        /// <summary>
        /// Nearest-rank on the sorted durations, index clamped to the last element — carried over
        /// exactly from the shell version's <c>ds[min(len(ds)-1, int(len(ds)*p))]</c> so a p95 read
        /// today is the same statistic as one read before the port.
        /// </summary>
        public double Percentile(double p)
        {
            if (Spans.Count == 0) return 0;

            var sorted = Spans.Select(x => x.Milliseconds).Order().ToArray();
            var index = Math.Min(sorted.Length - 1, (int)(sorted.Length * p));
            return sorted[index];
        }

        public double Max => Spans.Count == 0 ? 0 : Spans.Max(x => x.Milliseconds);

        /// <summary>Slowest first, then by operation and trace id so the order is deterministic.</summary>
        public IEnumerable<Span> Slowest(int count) => Spans
            .OrderByDescending(x => x.Milliseconds)
            .ThenBy(x => x.Operation, StringComparer.Ordinal)
            .ThenBy(x => x.TraceId, StringComparer.Ordinal)
            .Take(count);
    }

    /// <summary>
    /// Every span in a Jaeger <c>/api/traces</c> payload. Durations are microseconds on the wire.
    /// A payload that does not parse is an empty result with a reason, never a zero-span summary
    /// that reads like a quiet system.
    /// </summary>
    public static bool TrySummarise(string json, out Summary summary, out string problem)
    {
        summary = new Summary([]);
        problem = "";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            problem = $"Jaeger did not return JSON ({e.Message})";
            return false;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                problem = "the payload carried no 'data' array";
                return false;
            }

            var spans = new List<Span>();

            foreach (var trace in data.EnumerateArray())
            {
                var traceId = trace.TryGetProperty("traceID", out var t) ? t.GetString() ?? "" : "";

                if (!trace.TryGetProperty("spans", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var span in list.EnumerateArray())
                {
                    if (!span.TryGetProperty("duration", out var d) || !d.TryGetDouble(out var micros)) continue;

                    var operation = span.TryGetProperty("operationName", out var o) ? o.GetString() ?? "" : "";
                    spans.Add(new Span(micros / 1000.0, operation, traceId));
                }
            }

            summary = new Summary(spans);
            return true;
        }
    }

    /// <summary>
    /// Operation names from <c>/api/operations</c>. Jaeger has returned both bare strings and
    /// <c>{"name": …}</c> objects across versions, so both are accepted — the shell version did the
    /// same, and it is the one piece of defensiveness there worth keeping.
    /// </summary>
    public static IReadOnlyList<string> Operations(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n)
                    ? n.GetString()
                    : x.GetString())
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static int Run(int minutes, string service, string operation, bool raw)
    {
        // A LIVE pod, not `.items[0]` — the same rule every other verb here applies. A Jaeger
        // pod that is Running but not yet Ready answers with a connection refused that reads as
        // "there are no traces".
        var pods = ProcessRunner.Kubectl("get", "pods", "-l", "app=jaeger", "-o", "json");
        if (!pods.Ok)
        {
            return Refuse($"kubectl could not list the jaeger pods: {pods.StdErr.Trim()}");
        }

        if (!PodSelection.TrySelectSingle(pods.StdOut, out var selected, out var problem) || selected is null)
        {
            return Refuse($"no live jaeger pod ({problem}). kubectl apply -f k8s/jaeger.yaml");
        }

        var pod = selected.Name;

        if (raw)
        {
            var payload = Fetch(pod, $"/api/traces?service={service}&lookback={minutes}m&limit=2000&operation={operation}");
            if (!payload.Ok) return Refuse(payload.Problem);

            Console.WriteLine(payload.Value);
            return 0;
        }

        Console.WriteLine($"== operations seen by Jaeger (service={service}, last {minutes}m) ==");

        var operations = Fetch(pod, $"/api/operations?service={service}");
        if (operations.Ok)
        {
            var names = Operations(operations.Value);
            if (names.Count == 0)
            {
                Console.WriteLine("  (none — is SIM_OTLP_ENDPOINT set on the churnsim deployment?)");
            }

            foreach (var name in names) Console.WriteLine($"  {name}");
        }
        else
        {
            Console.WriteLine($"  (could not read them: {operations.Problem})");
        }

        Console.WriteLine();
        Console.WriteLine($"== {operation} span durations ==");

        var traces = Fetch(pod, $"/api/traces?service={service}&lookback={minutes}m&limit=2000&operation={operation}");
        if (!traces.Ok) return Refuse(traces.Problem);

        if (!TrySummarise(traces.Value, out var summary, out var parseProblem))
        {
            return Refuse(parseProblem);
        }

        if (summary.Spans.Count == 0)
        {
            // Zero spans is an ANSWER, and a misleading one if it is printed as a clean table with
            // three zeros in it. Say what it means instead, and do not exit 0 on it.
            return Refuse(
                $"Jaeger returned no '{operation}' spans in the last {minutes}m. Either nothing " +
                "exercised that path, or SIM_OTLP_ENDPOINT is not set on the churnsim deployment.");
        }

        Console.WriteLine($"  {summary.Spans.Count} spans; slowest:");
        foreach (var span in summary.Slowest(15))
        {
            Console.WriteLine($"    {span.Milliseconds.ToString("F1", CultureInfo.InvariantCulture),9} ms  " +
                              $"{span.Operation}  trace={span.TraceId}");
        }

        Console.WriteLine($"  p50 {F(summary.Percentile(0.5))} ms   p95 {F(summary.Percentile(0.95))} ms   " +
                          $"max {F(summary.Max)} ms");

        return 0;

        static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
    }

    private sealed record Fetched(bool Ok, string Value, string Problem);

    /// <summary>
    /// <c>wget</c> inside the Jaeger pod. Jaeger's UI port is a ClusterIP service with no ingress,
    /// so this is the only route to it that does not require standing up a port-forward whose
    /// lifetime nobody manages.
    /// </summary>
    private static Fetched Fetch(string pod, string path)
    {
        var result = ProcessRunner.Kubectl("exec", pod, "--", "wget", "-qO-", $"http://localhost:16686{path}");

        if (!result.Ok)
        {
            var detail = result.StdErr.Trim();
            return new Fetched(false, "",
                $"wget failed inside {pod}: {(detail.Length == 0 ? $"exit {result.ExitCode}" : detail)}");
        }

        return result.StdOut.Trim().Length == 0
            ? new Fetched(false, "", $"Jaeger returned an empty body for {path}")
            : new Fetched(true, result.StdOut, "");
    }

    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"traces: {problem}");
        return 2;
    }
}
