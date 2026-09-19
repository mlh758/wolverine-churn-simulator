using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>
/// The store side of a measurement: which arm is deployed, and what the assignment table says.
///
/// psql inside the pg pod, or <c>safetylab query</c> inside the monitor pod.
///
/// The query text is FROZEN and asserted so: every number in RESULTS.md was taken through it, and
/// rewording one breaks comparability with the whole existing record for no gain.
/// </summary>
public static class StoreQueries
{
    /// <summary>
    /// Verbatim from backend.sh's <c>db_assigned</c>. Do not reflow it.
    /// </summary>
    public const string AssignedSql =
        @"select a.id || chr(9) || n.description
                    from wolverine.wolverine_node_assignments a
                    join wolverine.wolverine_nodes n on n.id = a.node_id
                   where a.id like 'sim://%';";

    /// <summary>Frozen, same rule as <see cref="AssignedSql"/>.</summary>
    public const string PlacedSql =
        @"select count(*) from wolverine.wolverine_node_assignments where id like 'sim://%';";

    public sealed record Outcome<T>(T? Value, string Problem)
    {
        public bool Ok => Problem.Length == 0;

        public static Outcome<T> Good(T value) => new(value, "");
        public static Outcome<T> Bad(string problem) => new(default, problem);
    }

    // ------------------------------------------------------------------ backend

    /// <summary>
    /// Which arm is deployed, read from the deployment rather than from an argument — the one
    /// thing that must never happen is measuring one backend and filing it as the other.
    ///
    /// "No SIM_BACKEND declared" and "kubectl did not answer" are separate outcomes; only the
    /// first has a default.
    /// </summary>
    public static Outcome<string> Backend()
    {
        // An explicit override, so an operator can still pin an arm.
        var environment = Environment.GetEnvironmentVariable("SIM_BACKEND");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            var value = environment.Trim();
            return value is Backends.Postgres or Backends.RavenDb
                ? Outcome<string>.Good(value)
                : Outcome<string>.Bad(
                    $"SIM_BACKEND is set to '{value}', which is neither " +
                    $"'{Backends.Postgres}' nor '{Backends.RavenDb}'");
        }

        var result = Workload();
        if (!result.Ok)
        {
            return Outcome<string>.Bad($"the arm under test is unknown: {result.Problem}");
        }

        return Outcome<string>.Good(BackendFromDeployment(result.Value!));
    }

    /// <summary>
    /// The churnsim workload document, whichever kind it is. The single-store arms run a
    /// Deployment; the replicated RavenDB arm runs a StatefulSet, because each pod has to be
    /// pinned to one store member by ordinal and a Deployment's pods have no ordinal. Both carry
    /// the same pod template shape, so every env-var read below works on either.
    /// </summary>
    public static Outcome<string> Workload()
    {
        var deployment = ProcessRunner.Kubectl("get", "deployment", "churnsim", "-o", "json");
        if (deployment.Ok) return Outcome<string>.Good(deployment.StdOut);

        var statefulSet = ProcessRunner.Kubectl("get", "statefulset", "churnsim", "-o", "json");
        if (statefulSet.Ok) return Outcome<string>.Good(statefulSet.StdOut);

        return Outcome<string>.Bad(
            "could not read a churnsim deployment or statefulset: " +
            $"{Summarise(deployment.StdErr)} / {Summarise(statefulSet.StdErr)}");
    }

    /// <summary>
    /// A deployment predating the RavenDB arm carries no SIM_BACKEND and was PostgreSQL. That
    /// default applies ONLY to a deployment that was successfully read.
    /// </summary>
    public static string BackendFromDeployment(string deploymentJson)
        => EnvFromDeployment(deploymentJson, "SIM_BACKEND") ?? Backends.Postgres;

    /// <summary>
    /// One env var's value from a deployment document, or null when it is absent or has no literal
    /// value. A <c>valueFrom</c> entry (the downward API, which POD_NAME and POD_IP use) carries no
    /// <c>value</c> at all, and must not be read as an empty string.
    /// </summary>
    public static string? EnvFromDeployment(string deploymentJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(deploymentJson);

            var env = doc.RootElement
                .GetProperty("spec").GetProperty("template").GetProperty("spec")
                .GetProperty("containers")[0];

            if (env.TryGetProperty("env", out var variables) && variables.ValueKind == JsonValueKind.Array)
            {
                foreach (var variable in variables.EnumerateArray())
                {
                    if (variable.TryGetProperty("name", out var key) &&
                        key.GetString() == name &&
                        variable.TryGetProperty("value", out var value) &&
                        value.GetString() is { Length: > 0 } literal)
                    {
                        return literal;
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // An unreadable deployment document is the legacy shape as far as this is concerned;
            // the caller already knows kubectl answered, which is the distinction that mattered.
        }

        return null;
    }

    // ----------------------------------------------------------------- assigned

    // -------------------------------------------------------------- agent count

    /// <summary>
    /// <c>SIM_AGENT_COUNT</c>, read from the deployment rather than assumed. ChurnSim's own
    /// default when the var is absent is 20, so there is no safe constant to fall back to.
    /// </summary>
    public static Outcome<int> AgentCount()
    {
        var result = Workload();
        if (!result.Ok)
        {
            return Outcome<int>.Bad(result.Problem);
        }

        var value = EnvFromDeployment(result.Value!, "SIM_AGENT_COUNT");
        if (value is null)
        {
            return Outcome<int>.Bad(
                "the churnsim deployment declares no SIM_AGENT_COUNT, so how many agents a settled " +
                "cluster should have placed is unknown. Pass --expect to say.");
        }

        return int.TryParse(value, out var count) && count > 0
            ? Outcome<int>.Good(count)
            : Outcome<int>.Bad($"SIM_AGENT_COUNT is '{value}', which is not a positive integer");
    }

    /// <summary>Count of placed sim:// agents, on whichever arm is deployed.</summary>
    public static Outcome<int> Placed(string backend)
    {
        Outcome<string> raw;

        if (backend == Backends.RavenDb)
        {
            var pod = LivePod("app=safetylab");
            if (!pod.Ok)
            {
                return Outcome<int>.Bad(
                    "the RavenDB arm reads the store through the safetylab pod, and there is none " +
                    $"({pod.Problem}). Run ./scripts/monitor.sh deploy first.");
            }

            var result = ProcessRunner.Kubectl("exec", pod.Value!, "--", "dotnet", "safetylab.dll", "query", "placed");
            raw = result.Ok
                ? Outcome<string>.Good(result.StdOut)
                : Outcome<string>.Bad($"safetylab query failed in {pod.Value}: {Summarise(result.StdErr)}");
        }
        else
        {
            var pod = LivePod("app=pg");
            if (!pod.Ok) return Outcome<int>.Bad($"no live PostgreSQL pod: {pod.Problem}");

            var result = ProcessRunner.Kubectl(
                "exec", pod.Value!, "--", "psql", "-U", "postgres", "-d", "churnsim", "-qAt", "-c", PlacedSql);
            raw = result.Ok
                ? Outcome<string>.Good(result.StdOut)
                : Outcome<string>.Bad($"psql failed in {pod.Value}: {Summarise(result.StdErr)}");
        }

        if (!raw.Ok) return Outcome<int>.Bad(raw.Problem);

        var text = new string((raw.Value ?? "").Where(char.IsDigit).ToArray());

        // An unparseable count is NOT zero. Zero is a real cluster state (nothing placed yet); a
        // failed read is not, and treating them alike is how a poll loop waits out its whole
        // timeout against a store it cannot reach and reports "never settled".
        return int.TryParse(text, out var placed)
            ? Outcome<int>.Good(placed)
            : Outcome<int>.Bad($"the store did not answer with a count (got '{(raw.Value ?? "").Trim()}')");
    }

    /// <summary>
    /// <c>agentUri, pod</c> for every placed sim:// agent. A failure here is an OUTCOME, not an
    /// empty result: an empty assigned set against an empty running set compares perfectly clean.
    /// </summary>
    public static Outcome<IReadOnlyList<(string Agent, string Pod)>> Assigned(string backend)
    {
        var raw = backend == Backends.RavenDb ? AssignedFromRavenDb() : AssignedFromPostgres();
        if (!raw.Ok) return Outcome<IReadOnlyList<(string, string)>>.Bad(raw.Problem);

        return Outcome<IReadOnlyList<(string, string)>>.Good(ParseTsv(raw.Value!));
    }

    private static Outcome<string> AssignedFromPostgres()
    {
        var pod = LivePod("app=pg");
        if (!pod.Ok) return Outcome<string>.Bad($"no live PostgreSQL pod: {pod.Problem}");

        var result = ProcessRunner.Kubectl(
            "exec", pod.Value!, "--", "psql", "-U", "postgres", "-d", "churnsim", "-qAt", "-c", AssignedSql);

        return result.Ok
            ? Outcome<string>.Good(result.StdOut)
            : Outcome<string>.Bad($"psql failed in {pod.Value}: {Summarise(result.StdErr)}");
    }

    private static Outcome<string> AssignedFromRavenDb()
    {
        // RavenDB has no psql and its container ships no client worth depending on, so the
        // equivalent is `safetylab query` inside the already-deployed monitor pod. Worth saying
        // out loud rather than failing obscurely: this arm cannot be measured without it.
        var pod = LivePod("app=safetylab");
        if (!pod.Ok)
        {
            return Outcome<string>.Bad(
                "the RavenDB arm reads the store through the safetylab pod, and there is none " +
                $"({pod.Problem}). Run ./scripts/monitor.sh deploy first.");
        }

        var result = ProcessRunner.Kubectl("exec", pod.Value!, "--", "dotnet", "safetylab.dll", "query", "assigned");

        return result.Ok
            ? Outcome<string>.Good(result.StdOut)
            : Outcome<string>.Bad($"safetylab query failed in {pod.Value}: {Summarise(result.StdErr)}");
    }

    /// <summary>
    /// One live pod for a label. Exec-ing into a terminating predecessor fails with "cannot exec
    /// into a container in a completed pod", which reads as "the store is unreachable".
    /// </summary>
    private static Outcome<string> LivePod(string label)
    {
        var result = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!result.Ok) return Outcome<string>.Bad($"kubectl failed: {Summarise(result.StdErr)}");

        return PodSelection.TrySelectSingle(result.StdOut, out var pod, out var problem)
            ? Outcome<string>.Good(pod!.Name)
            : Outcome<string>.Bad(problem);
    }

    /// <summary>
    /// Two tab-separated columns; a row of any other shape is dropped rather than guessed at.
    /// </summary>
    public static IReadOnlyList<(string Agent, string Pod)> ParseTsv(string text)
    {
        var rows = new List<(string, string)>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length == 0) continue;

            var parts = trimmed.Split('\t');
            if (parts.Length != 2) continue;

            rows.Add((parts[0], parts[1]));
        }

        return rows;
    }

    private static string Summarise(string stderr)
    {
        var text = stderr.Trim();
        if (text.Length == 0) return "no output on stderr";

        var firstLine = text.Split('\n')[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine;
    }
}
