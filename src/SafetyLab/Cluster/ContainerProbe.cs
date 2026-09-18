using System.Text.Json;

namespace SafetyLab.Cluster;

/// <summary>The outcome of resolving a container to a host process, and why it failed if it did.</summary>
public sealed record HostProcess(int Pid, string CommandLine)
{
    public static HostProcess None { get; } = new(0, "");
}

/// <summary>
/// Turning a container id into a host pid that is safe to <c>kill -9</c>.
///
/// This file exists because of a specific near-miss. The shell version ran
/// <c>crictl inspect $CID | grep -m1 '"pid"' | tr -dc '0-9'</c>, and the first <c>"pid"</c> in that
/// document is a namespace descriptor whose value is <c>1</c> — so the fault injector ran
/// <c>kill -9 1</c> against the minikube node's own init. Nothing died, purely because the kernel
/// refuses unhandled signals to PID 1 from inside its namespace. The experiment then reported a
/// failover measurement for a kill that never happened.
///
/// Two separate failures there, and this type is built to make both impossible: reading a
/// structured document with a text tool, and handing an unvalidated number to <c>kill</c>.
/// </summary>
public static class ContainerProbe
{
    /// <summary>
    /// The container process's pid, read from <c>.info.pid</c> — the only field that means it.
    /// Returns null rather than guessing, and never falls back to "some pid found somewhere".
    /// </summary>
    public static int? HostPidFrom(string crictlInspectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(crictlInspectJson);

            if (!doc.RootElement.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("pid", out var pid))
            {
                return null;
            }

            return pid.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Is this pid safe to kill, and is it the process we actually meant?
    ///
    /// <paramref name="expectedInCommandLine"/> is checked against <c>/proc/&lt;pid&gt;/cmdline</c>
    /// by the caller. A fault injector aimed at the wrong process either destroys the rig or,
    /// worse, injects nothing and leaves a plausible number behind.
    /// </summary>
    public static bool IsSafeToKill(int? pid, string commandLine, string expectedInCommandLine, out string problem)
    {
        problem = "";

        if (pid is null)
        {
            problem = "could not resolve a host pid from the container inspect output " +
                      "(no .info.pid) — refusing to guess";
            return false;
        }

        if (pid <= 1)
        {
            problem = $"resolved host pid {pid}, which is init or invalid — refusing to kill it. " +
                      "This is what a mis-parsed inspect document looks like";
            return false;
        }

        if (!commandLine.Contains(expectedInCommandLine, StringComparison.Ordinal))
        {
            problem = $"host pid {pid} has command line '{commandLine.Trim()}', which does not " +
                      $"contain '{expectedInCommandLine}' — refusing to kill the wrong process";
            return false;
        }

        return true;
    }
}
