using System.Diagnostics;

namespace SafetyLab.Cluster;

/// <summary>
/// Running an external command and getting its output back as data.
///
/// The shell scripts keep doing what shell is good at — invoking kubectl, minikube and podman.
/// What moved in here is every DECISION made about what that output means, because all three
/// defects in this rig's fault-injection layer were decisions taken by a text tool against
/// structured data.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static Result Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"could not start {file}");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // minikube ssh rides a tty and brings carriage returns back with it, which turn an
        // otherwise clean integer into something int.Parse rejects.
        return new Result(process.ExitCode, stdout.Replace("\r", ""), stderr.Replace("\r", ""));
    }

    /// <summary>
    /// kubectl, pinned to the minikube context exactly as every script in this repo pins it. A
    /// bare kubectl here would aim these commands at whatever the ambient kubeconfig points to,
    /// which for a tool whose job includes `kill -9` is not a risk worth carrying.
    /// </summary>
    public static Result Kubectl(params string[] args)
        => Run("minikube", ["kubectl", "--", "--context=minikube", .. args]);

    public static Result MinikubeSsh(string command)
        => Run("minikube", "ssh", "--", command);

    /// <summary>
    /// Like <see cref="Kubectl"/>, but stdout is streamed straight to a file instead of buffered
    /// into a string. A settled pod's log is tens of megabytes of JSON; reading it into memory only
    /// to write it out again doubles that for no reason, and the <c>\r</c> rewrite that helps
    /// `minikube ssh` would be editing captured evidence.
    /// </summary>
    public static Result KubectlToFile(string path, params string[] args)
    {
        var info = new ProcessStartInfo("minikube")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in new[] { "kubectl", "--", "--context=minikube" }.Concat(args))
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException("could not start minikube kubectl");

        // stderr is drained on another thread: a process that fills its stderr pipe while nobody
        // reads it blocks forever, and `kubectl logs` is chatty enough to do exactly that.
        var stderr = process.StandardError.ReadToEndAsync();

        using (var file = File.Create(path))
        {
            process.StandardOutput.BaseStream.CopyTo(file);
        }

        process.WaitForExit();
        return new Result(process.ExitCode, "", stderr.GetAwaiter().GetResult().Replace("\r", ""));
    }
}

/// <summary>
/// The host-side verbs: resolving a pod to act on, and resolving a container to a killable host
/// process. Both were previously done in shell and both got it wrong.
/// </summary>
public static class HostTools
{
    /// <summary>Print the live pods matching a label, one per line.</summary>
    public static int Pods(string label, bool liveOnly)
    {
        var result = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!result.Ok)
        {
            Console.Error.WriteLine($"pods: kubectl failed: {result.StdErr.Trim()}");
            return 2;
        }

        var pods = liveOnly ? PodSelection.Live(result.StdOut) : PodSelection.Parse(result.StdOut);
        foreach (var pod in pods) Console.WriteLine(pod.Name);
        return 0;
    }

    /// <summary>
    /// Exactly one live pod for a label, or a non-zero exit and an explanation. This is what
    /// `kubectl ... -o jsonpath='{.items[0].metadata.name}'` was pretending to be.
    /// </summary>
    public static int PickPod(string label)
    {
        var result = ProcessRunner.Kubectl("get", "pods", "-l", label, "-o", "json");
        if (!result.Ok)
        {
            Console.Error.WriteLine($"pick-pod: kubectl failed: {result.StdErr.Trim()}");
            return 2;
        }

        if (!PodSelection.TrySelectSingle(result.StdOut, out var pod, out var problem))
        {
            Console.Error.WriteLine($"pick-pod: no usable pod for '{label}': {problem}");
            return 2;
        }

        Console.WriteLine(pod!.Name);
        return 0;
    }

    /// <summary>
    /// The host pid of a pod's container, resolved from <c>.info.pid</c> and validated before it
    /// is printed — so a caller that pipes this into <c>kill -9</c> cannot be handed init.
    /// </summary>
    public static int HostPid(string pod, string containerName, string expectedInCommandLine)
    {
        var ids = ProcessRunner.MinikubeSsh(
            $"sudo crictl ps -q --name {containerName} --label io.kubernetes.pod.name={pod}");

        var containerId = ids.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (containerId is null)
        {
            Console.Error.WriteLine($"host-pid: no running container '{containerName}' in pod '{pod}'");
            return 2;
        }

        var inspect = ProcessRunner.MinikubeSsh($"sudo crictl inspect {containerId}");
        if (!inspect.Ok)
        {
            Console.Error.WriteLine($"host-pid: crictl inspect failed: {inspect.StdErr.Trim()}");
            return 2;
        }

        var pid = ContainerProbe.HostPidFrom(inspect.StdOut);

        var cmdline = pid is null
            ? ""
            : ProcessRunner.MinikubeSsh($"sudo tr '\\0' ' ' < /proc/{pid}/cmdline").StdOut;

        if (!ContainerProbe.IsSafeToKill(pid, cmdline, expectedInCommandLine, out var problem))
        {
            Console.Error.WriteLine($"host-pid: {problem}");
            return 2;
        }

        Console.WriteLine(pid);
        return 0;
    }
}
