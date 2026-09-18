using System.Globalization;

namespace SafetyLab.Cluster;

/// <summary>
/// Wait for every sim agent to be placed, and then for the placement to hold still.
///
///   exit 0 — settled (elapsed seconds on stdout)
///   exit 1 — never settled (-1 on stdout)
///   exit 2 — COULD NOT MEASURE
///
/// The target comes from the deployment's <c>SIM_AGENT_COUNT</c>, and a store that cannot be read
/// is exit 2 rather than "0 placed" — otherwise an outage waits out the whole timeout and reports
/// itself as a convergence failure.
/// </summary>
public static class Settle
{
    /// <summary>One poll: how long in, and what the store said.</summary>
    public sealed record Sample(int ElapsedSeconds, int Placed);

    public sealed record Result(bool Settled, int ElapsedSeconds, IReadOnlyList<Sample> Series);

    /// <summary>
    /// The expected count, held across <paramref name="stableFor"/> consecutive polls. Placement
    /// passes THROUGH the target during a rollout, so one sample at the right number is not
    /// convergence.
    /// </summary>
    public static bool IsSettled(IReadOnlyList<Sample> series, int expected, int stableFor)
    {
        if (series.Count < stableFor) return false;

        return series.TakeLast(stableFor).All(x => x.Placed == expected);
    }

    public static int Run(int? expect, int timeoutSeconds, int pollSeconds, int stableFor, string? seriesPath)
    {
        var backend = StoreQueries.Backend();
        if (!backend.Ok) return Refuse(backend.Problem);

        int target;
        if (expect is { } given)
        {
            target = given;
        }
        else
        {
            var declared = StoreQueries.AgentCount();
            if (!declared.Ok) return Refuse(declared.Problem);
            target = declared.Value;
        }

        var started = DateTimeOffset.UtcNow;
        var series = new List<Sample>();
        var deadline = started.AddSeconds(timeoutSeconds);

        // Consecutive read failures are their own outcome. One is a blip worth another poll; a
        // run of them means the store is gone, and waiting out the timeout to call that "never
        // settled" would file a store outage as a convergence result.
        var consecutiveFailures = 0;
        const int failureLimit = 5;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var elapsed = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
            var placed = StoreQueries.Placed(backend.Value!);

            if (!placed.Ok)
            {
                if (++consecutiveFailures >= failureLimit)
                {
                    Write(seriesPath, series);
                    return Refuse($"the store failed {consecutiveFailures} reads in a row: {placed.Problem}");
                }

                Thread.Sleep(TimeSpan.FromSeconds(pollSeconds));
                continue;
            }

            consecutiveFailures = 0;
            series.Add(new Sample(elapsed, placed.Value));

            if (IsSettled(series, target, stableFor))
            {
                Write(seriesPath, series);
                Console.WriteLine(elapsed.ToString(CultureInfo.InvariantCulture));
                return 0;
            }

            Thread.Sleep(TimeSpan.FromSeconds(pollSeconds));
        }

        Write(seriesPath, series);

        var last = series.Count > 0 ? series[^1].Placed : -1;
        Console.Error.WriteLine(
            $"settle: {target} agents never held still for {stableFor} polls within {timeoutSeconds}s " +
            $"(last saw {last} placed, {series.Count} polls)");

        // -1 is what the settle_s column carries for an iteration that did not converge.
        Console.WriteLine("-1");
        return 1;
    }

    /// <summary>
    /// The placement series, one poll per line. A stalled iteration (831 s against 30-51 s, twice,
    /// on independent clusters) looks identical to a slow one in the final number; the series
    /// separates a flat wait from a crawl.
    /// </summary>
    private static void Write(string? path, IReadOnlyList<Sample> series)
    {
        if (string.IsNullOrEmpty(path)) return;

        File.WriteAllLines(path, series.Select(x => $"{x.ElapsedSeconds}\t{x.Placed}"));
    }

    private static int Refuse(string problem)
    {
        Console.Error.WriteLine($"settle: REFUSED — {problem}");
        Console.Error.WriteLine("settle: nothing was waited for. This is not 'the cluster never converged'.");
        return 2;
    }
}
