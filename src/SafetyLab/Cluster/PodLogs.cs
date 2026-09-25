using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafetyLab.Cluster;

/// <summary>
/// The host side of the node log tailer (k8s/logtail.yaml): pull every churnsim container's
/// log — live, replaced, or killed — off the node and decode it into the run directory as
/// <c>raw.&lt;pod&gt;.&lt;attempt&gt;.jsonl</c>, the same lines <c>kubectl logs</c> would print.
///
/// The tailer writes one directory per run, named by its <c>LOGTAIL_RUN</c> environment variable.
/// <see cref="Start"/> sets it and rolls the pod; everything else reads it back from the live pod,
/// so what is pulled is always what the tailer is actually writing and never what a shell
/// variable says it should be.
///
/// Every refusal here is a sentinel (docs/harness-traps.md, rule 1). The tailer is a copy, and a
/// copy that is silently missing a pod produces a run directory that reads as complete: a
/// leaderless window with no log from the leader, a duplicate with only one side's residency.
/// So a live churnsim pod whose current attempt has no file on the node is not a warning, it is
/// exit 2 — the rig could not capture what it claims to have captured.
/// </summary>
public static partial class PodLogs
{
    public const string TailerLabel = "app=logtail";
    public const string TailerContainer = "fluent-bit";
    public const string ShelfContainer = "shelf";
    public const string RunVariable = "LOGTAIL_RUN";
    public const string NodeDirectory = "/data/podlogs";

    /// <summary>A file the tailer holds on the node, named by the container attempt it copies.</summary>
    public sealed record NodeFile(string Pod, int Attempt, string Name);

    // --------------------------------------------------------------- decisions

    /// <summary>
    /// A run name is a directory on the node and an environment value, and it is typed by hand:
    /// letters, digits, dot, dash and underscore, not starting with a dot or a dash. Anything
    /// else is refused rather than escaped.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RunName();

    public static bool IsValidRunName(string name) => RunName().IsMatch(name);

    /// <summary>Which files in a node directory listing are logs; the rest are the tailer's own.</summary>
    public static IReadOnlyList<NodeFile> Files(IEnumerable<string> names)
    {
        var files = new List<NodeFile>();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (CriLog.TryParseFileName(name, out var pod, out var attempt))
            {
                files.Add(new NodeFile(pod, attempt, name));
            }
        }

        return files.OrderBy(x => x.Pod, StringComparer.Ordinal).ThenBy(x => x.Attempt).ToList();
    }

    /// <summary>
    /// Live pods the tailer is NOT following: no file for the pod's current attempt, which is its
    /// restart count. A pod's attempt 0 file being present while it runs attempt 1 is not cover.
    /// </summary>
    public static IReadOnlyList<string> Uncovered(IEnumerable<PodInfo> live, IReadOnlyList<NodeFile> files)
    {
        var have = files.Select(x => (x.Pod, x.Attempt)).ToHashSet();
        return live.Where(x => !have.Contains((x.Name, x.RestartCount)))
            .Select(x => $"{x.Name} (attempt {x.RestartCount})")
            .ToList();
    }

    /// <summary>How a file relates to the cluster right now, for the listing.</summary>
    public static string Describe(NodeFile file, IReadOnlyList<PodInfo> pods)
    {
        var pod = pods.FirstOrDefault(x => x.Name == file.Pod);
        if (pod is null) return "gone";
        if (file.Attempt < pod.RestartCount) return "previous";
        return pod.IsLive ? "live" : pod.Terminating ? "terminating" : pod.Phase.ToLowerInvariant();
    }

    /// <summary>
    /// The run the tailer pod is writing to, read from its own environment in a
    /// <c>kubectl get pods -o json</c> listing. Null when the pod is not there or carries no such
    /// variable — a caller must refuse on that, not fall back to a name it hoped for. Both the
    /// tailer and the init container that makes its directory carry the variable, and they must
    /// agree: <c>kubectl set env</c> that reached one and not the other would have the tailer
    /// open a directory nobody created.
    /// </summary>
    public static string? RunOf(string podsJson, string podName, out string problem)
    {
        problem = "";
        using var doc = JsonDocument.Parse(podsJson);

        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            problem = "not a pod list";
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("name", out var name) || name.GetString() != podName)
            {
                continue;
            }

            if (!item.TryGetProperty("spec", out var spec))
            {
                problem = $"{podName} has no spec";
                return null;
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var section in new[] { "containers", "initContainers" })
            {
                if (!spec.TryGetProperty(section, out var containers) || containers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var container in containers.EnumerateArray())
                {
                    var containerName = container.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string? value = null;
                    if (container.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var variable in env.EnumerateArray())
                        {
                            if (variable.TryGetProperty("name", out var vn) && vn.GetString() == RunVariable &&
                                variable.TryGetProperty("value", out var vv))
                            {
                                value = vv.GetString();
                            }
                        }
                    }

                    values[containerName] = value;
                }
            }

            if (!values.TryGetValue(TailerContainer, out var run) || string.IsNullOrEmpty(run))
            {
                problem = $"{podName} carries no {RunVariable} in its {TailerContainer} container";
                return null;
            }

            var disagreeing = values.Where(x => x.Value is not null && x.Value != run).Select(x => x.Key).ToList();
            if (disagreeing.Count > 0)
            {
                problem = $"{podName}: {TailerContainer} is on run '{run}' but " +
                          string.Join(", ", disagreeing.Select(x => $"{x} is on '{values[x]}'"));
                return null;
            }

            return run;
        }

        problem = $"{podName} is not in the listing";
        return null;
    }

    // -------------------------------------------------------------------- verbs

    public static int Status(string label)
    {
        if (!TryResolve(label, out var tailer, out var run, out var runs, out var files, out var pods, out var problem))
        {
            return Refuse(problem);
        }

        Console.WriteLine($"tailer   {tailer!.Name}");
        Console.WriteLine($"run      {run}   ({files!.Count} container log(s) in {NodeDirectory}/{run})");
        foreach (var file in files)
        {
            Console.WriteLine($"  {file.Pod,-40} attempt {file.Attempt}  {Describe(file, pods!)}");
        }

        var others = runs!.Where(x => x != run).ToList();
        if (others.Count > 0)
        {
            Console.WriteLine($"older    {string.Join(", ", others)}   (`podlogs pull --run <name>`; scripts/logtail.sh reset clears)");
        }

        var uncovered = Uncovered(pods!.Where(x => x.IsLive), files);
        if (uncovered.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("NOT FOLLOWED: " + string.Join(", ", uncovered));
            Console.WriteLine("  a live pod with no copy on the node -- a capture now would be missing it.");
            return 1;
        }

        Console.WriteLine($"every live '{label}' pod is being followed");
        return 0;
    }

    /// <summary>
    /// Begin a run: a fresh directory on the node that the tailer fills from the head of every
    /// container file the kubelet still has. Refuses a name already used, because the tailer
    /// would append to that directory's files and the earlier run's pods would read as this one's.
    /// </summary>
    public static int Start(string name, string label)
    {
        if (!IsValidRunName(name))
        {
            return Refuse($"'{name}' is not a run name: letters, digits, '.', '-' and '_', not starting with '.' or '-'");
        }

        if (!TryResolve(label, out var tailer, out var current, out var runs, out _, out _, out var problem))
        {
            return Refuse(problem);
        }

        if (runs!.Contains(name))
        {
            return Refuse(current == name
                ? $"the tailer is already on run '{name}'. A run is one directory; pick another name, " +
                  "or clear the node with scripts/logtail.sh reset"
                : $"{NodeDirectory}/{name} already exists on the node from an earlier run. Pick another " +
                  "name, or clear the node with scripts/logtail.sh reset");
        }

        var set = ProcessRunner.Kubectl("set", "env", "daemonset/logtail", $"{RunVariable}={name}");
        if (!set.Ok) return Refuse($"could not set {RunVariable} on the DaemonSet: {set.StdErr.Trim()}");

        var rolled = ProcessRunner.Kubectl("rollout", "status", "daemonset/logtail", "--timeout=180s");
        if (!rolled.Ok) return Refuse($"the tailer did not roll onto the new run: {rolled.StdErr.Trim()}");

        // Prove it from the pod that is running now, not from the command that was issued: the
        // new pod must be on this run, and it must have re-read every live pod's file. Reading
        // from the head takes it a moment after start.
        for (var attempt = 0; ; attempt++)
        {
            if (!TryResolve(label, out tailer, out current, out _, out var files, out var pods, out problem))
            {
                return Refuse(problem);
            }

            if (current != name)
            {
                return Refuse($"after the roll the tailer pod {tailer!.Name} is on run '{current}', not '{name}'");
            }

            var uncovered = Uncovered(pods!.Where(x => x.IsLive), files!);
            if (uncovered.Count == 0)
            {
                Console.WriteLine($"run {name} on {tailer!.Name}: following {files!.Count} container log(s)");
                return 0;
            }

            if (attempt >= 15)
            {
                return Refuse($"run '{name}' started but the tailer is not following: {string.Join(", ", uncovered)}");
            }

            Thread.Sleep(1000);
        }
    }

    public static int Pull(string directory, string label, string? run)
        => PullInto(directory, label, run, mustBeCurrent: false, out _);

    /// <summary>
    /// The capture's pod side: pull the run's logs into <c>&lt;dir&gt;/logs</c>, and harvest
    /// each into <c>&lt;dir&gt;/pods.&lt;pod&gt;.&lt;attempt&gt;.jsonl</c>, the records
    /// <c>check</c> reads. This replaced following each pod with <c>kubectl logs -f</c> from the
    /// moment the capture started: the node copy is complete whether or not anything was
    /// attached, so a capture no longer has to begin before the disturbance.
    /// </summary>
    public static int Harvest(string directory, string label, string run)
    {
        // By name, and the tailer must still be on it: if it has moved since the capture started
        // (a deploy re-applies the manifest; a kill script outside a capture rolls it), the pods
        // that ran after the move are in another directory and this run's pod side is not whole.
        var logs = Path.Combine(directory, "logs");
        var pulled = PullInto(logs, label, run, mustBeCurrent: true, out var files);
        if (pulled == 2) return 2;

        var blind = 0;
        Console.WriteLine();
        foreach (var file in files)
        {
            var raw = Path.Combine(logs, $"raw.{file.Pod}.{file.Attempt}.jsonl");
            var target = Path.Combine(directory, $"pods.{file.Pod}.{file.Attempt}.jsonl");

            int identities = 0, agents = 0, controls = 0;
            using (var writer = new StreamWriter(target, false, new System.Text.UTF8Encoding(false)))
            {
                foreach (var record in LogHarvest.Parse(file.Pod, File.ReadLines(raw)))
                {
                    switch (record)
                    {
                        case IdentityRecord: identities++; break;
                        case AgentEventRecord: agents++; break;
                        case ControlEventRecord: controls++; break;
                    }

                    writer.WriteLine(JsonSerializer.Serialize(record, record.GetType(), Json.Options));
                }
            }

            if (identities == 0) blind++;
            Console.WriteLine($"  {file.Pod,-40} attempt {file.Attempt}  {identities} identity, {agents,5} agent, {controls,5} control" +
                              (identities == 0 ? "   NO IDENTITY -- invisible to every pod-side check" : ""));
        }

        Console.WriteLine($"harvested {files.Count} container log(s) into {directory}");

        if (blind > 0)
        {
            // Written, but not whole: a log whose head is missing (rotated away before the run
            // began) carries no SIM-IDENTITY, and S3/S4/S6/S7 cannot attribute anything to it.
            Console.Error.WriteLine($"podlogs: {blind} container log(s) carry no SIM-IDENTITY; C0 will report the pod");
            return 1;
        }

        return pulled;
    }

    private static int PullInto(string directory, string label, string? run, bool mustBeCurrent,
        out IReadOnlyList<NodeFile> files)
    {
        files = [];
        if (!TryResolve(label, out var tailer, out var current, out var runs, out _, out var pods, out var problem))
        {
            return Refuse(problem);
        }

        run ??= current;
        if (mustBeCurrent && run != current)
        {
            return Refuse($"the tailer is on run '{current}', not '{run}': it moved after this run started " +
                          "(a `just deploy` re-applies k8s/logtail.yaml; a kill script outside a capture starts " +
                          $"its own run), so the pods that ran since are not in '{run}'");
        }

        if (!runs!.Contains(run))
        {
            return Refuse($"no run '{run}' on the node. Runs there: {string.Join(", ", runs)}");
        }

        if (!TryList(tailer!, run, out var listed, out problem)) return Refuse(problem);
        files = listed;

        if (files.Count == 0)
        {
            return Refuse($"run '{run}' holds no container log at all in {NodeDirectory}/{run}. " +
                          "Was the tailer following anything? `safetylab podlogs status`.");
        }

        // Coverage is checked before anything is written, and the files are decoded into a staging
        // directory and moved at the end, so a refusal leaves no half-filled run directory that a
        // later step could mistake for a capture. An older run cannot be judged against the pods
        // that are live now, so it is only the current run that is gated.
        if (run == current)
        {
            var uncovered = Uncovered(pods!.Where(x => x.IsLive), files);
            if (uncovered.Count > 0)
            {
                return Refuse("live pod(s) the tailer is not following: " + string.Join(", ", uncovered) +
                              ". Their log would be missing from this capture. `safetylab podlogs status`.");
            }
        }

        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, ".cri");
        Directory.CreateDirectory(staging);

        var malformedTotal = 0;
        try
        {
            foreach (var file in files)
            {
                var copied = Path.Combine(staging, file.Name);
                var fetch = ProcessRunner.KubectlToFile(copied, "exec", tailer!.Name, "-c", ShelfContainer, "--",
                    "cat", $"{NodeDirectory}/{run}/{file.Name}");
                if (!fetch.Ok)
                {
                    return Refuse($"could not read {file.Name} from the tailer pod: {fetch.StdErr.Trim()}");
                }

                var decoded = CriLog.Decode(File.ReadLines(copied));
                var target = Path.Combine(staging, $"raw.{file.Pod}.{file.Attempt}.jsonl");
                using (var writer = new StreamWriter(target, false, new System.Text.UTF8Encoding(false)))
                {
                    foreach (var entry in decoded.Entries) writer.WriteLine(entry.Text);
                }

                File.Delete(copied);
                malformedTotal += decoded.Malformed;

                Console.WriteLine(
                    $"  {file.Pod,-40} attempt {file.Attempt}  {decoded.Entries.Count,7} lines  " +
                    $"{Stamp(decoded.First)} .. {Stamp(decoded.Last)}  {Describe(file, pods!)}" +
                    (decoded.Joined > 0 ? $"  ({decoded.Joined} partial records joined)" : "") +
                    (decoded.Malformed > 0 ? $"  MALFORMED: {decoded.Malformed} records dropped" : ""));
            }

            foreach (var decodedFile in Directory.GetFiles(staging, "raw.*.jsonl"))
            {
                File.Move(decodedFile, Path.Combine(directory, Path.GetFileName(decodedFile)), overwrite: true);
            }
        }
        finally
        {
            Directory.Delete(staging, true);
        }

        if (malformedTotal > 0)
        {
            // Not a refusal: the files are written and mostly right. But the tailer's copy is meant to be
            // verbatim, so a record that did not decode is a defect in this pipeline, and the caller
            // gets told in the exit code rather than in a line they may not read.
            Console.Error.WriteLine($"podlogs: {malformedTotal} record(s) did not fit the CRI format and were dropped");
            return 1;
        }

        Console.WriteLine($"pulled {files.Count} container log(s) of run {run} into {directory}");
        return 0;
    }

    // ------------------------------------------------------------------ plumbing

    private static bool TryResolve(string label, out PodInfo? tailer, out string? run,
        out IReadOnlyList<string>? runs, out IReadOnlyList<NodeFile>? files,
        out IReadOnlyList<PodInfo>? pods, out string problem)
    {
        tailer = null;
        run = null;
        runs = null;
        files = null;
        pods = null;

        var tailers = ProcessRunner.Kubectl("get", "pods", "-l", TailerLabel, "-o", "json");
        if (!tailers.Ok)
        {
            problem = $"kubectl could not list the tailer pods: {tailers.StdErr.Trim()}";
            return false;
        }

        if (!PodSelection.TrySelectSingle(tailers.StdOut, out tailer, out var why))
        {
            problem = $"no live log tailer ({why}). Deploy it: kubectl apply -f k8s/logtail.yaml (just logtail-deploy)";
            return false;
        }

        run = RunOf(tailers.StdOut, tailer!.Name, out problem);
        if (run is null) return false;

        // Run directories on the node. `find` rather than `ls`, so the tailer's own files at the
        // top level (none today, but a future fluent-bit could put one there) are not runs.
        var listing = ProcessRunner.Kubectl("exec", tailer.Name, "-c", ShelfContainer, "--",
            "find", NodeDirectory, "-mindepth", "1", "-maxdepth", "1", "-type", "d");
        if (!listing.Ok)
        {
            problem = $"could not list {NodeDirectory} through the tailer pod: {listing.StdErr.Trim()}";
            return false;
        }

        runs = listing.StdOut.Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select(Path.GetFileName)
            .Where(x => IsValidRunName(x!))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (!TryList(tailer, run, out files, out problem)) return false;

        var listed = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!listed.Ok)
        {
            problem = $"kubectl could not list pods for '{label}': {listed.StdErr.Trim()}";
            return false;
        }

        pods = PodSelection.Parse(listed.StdOut);
        problem = "";
        return true;
    }

    private static bool TryList(PodInfo tailer, string run, out IReadOnlyList<NodeFile> files, out string problem)
    {
        files = [];
        problem = "";

        var listing = ProcessRunner.Kubectl("exec", tailer.Name, "-c", ShelfContainer, "--",
            "sh", "-c", $"ls -1 {NodeDirectory}/{run} 2>/dev/null || true");
        if (!listing.Ok)
        {
            problem = $"could not list {NodeDirectory}/{run} through the tailer pod: {listing.StdErr.Trim()}";
            return false;
        }

        files = Files(listing.StdOut.Split('\n'));
        return true;
    }

    private static string Stamp(DateTimeOffset? ts)
        => ts?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";

    private static int Refuse(string why)
    {
        Console.Error.WriteLine($"podlogs: {why}");
        return 2;
    }
}
