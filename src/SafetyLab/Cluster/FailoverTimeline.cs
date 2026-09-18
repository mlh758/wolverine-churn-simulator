namespace SafetyLab.Cluster;

/// <summary>One poll during a failover experiment.</summary>
public sealed record FailoverSample(
    int ElapsedSeconds,
    LeaderState Leader,
    LockState? Lock,
    int? Placed)
{
    public string ToTsv(DateTimeOffset now)
    {
        var holder = Lock?.Holder?.ToString() ?? "-";
        var ttl = Lock?.SecondsToExpiry(now) is { } s ? $"{s:F0}" : "-";
        var pod = Leader is LeaderState.Held h ? h.Pod : "none";
        var node = Leader.NodeIdOrNull?.ToString() ?? "-";
        return $"{ElapsedSeconds}\t{pod}\t{node}\t{holder}\t{ttl}\t{Placed?.ToString() ?? "-"}";
    }

    public const string TsvHeader = "elapsed_s\tleader_pod\tleader_node\tlock_holder\tlock_expires_in_s\tplaced";
}

/// <summary>Whether the fault injector actually did what the run claims it did.</summary>
public sealed record NemesisVerdict(bool Valid, string Detail)
{
    /// <summary>
    /// <c>NodeStopped</c> is written by the <c>NodeStopped()</c> observer callback, which only the
    /// graceful shutdown path reaches — a SIGKILLed process cannot write one. So a NodeStopped
    /// appearing across a run that claims to be an ungraceful kill proves the victim shut down
    /// cleanly, released its lock, and never exercised the expiry path at all.
    ///
    /// This check exists because that is exactly what happened: `kubectl delete pod --force
    /// --grace-period=0` still delivers SIGTERM, and the resulting 6-second "failover" was a clean
    /// handover wearing the label of a crash.
    /// </summary>
    public static NemesisVerdict Evaluate(bool ungraceful, int stoppedBefore, int stoppedAfter)
    {
        if (!ungraceful)
        {
            return new NemesisVerdict(true,
                $"graceful control arm; NodeStopped {stoppedBefore} -> {stoppedAfter} as expected");
        }

        if (stoppedAfter > stoppedBefore)
        {
            return new NemesisVerdict(false,
                $"a NodeStopped record was written ({stoppedBefore} -> {stoppedAfter}), so the victim " +
                "shut down GRACEFULLY and released its lock. The expiry path was never exercised and " +
                "the window below is a clean-handover time, not a failover measurement");
        }

        return new NemesisVerdict(true,
            $"no NodeStopped written ({stoppedBefore} -> {stoppedAfter}); the victim died without " +
            "running shutdown, as intended");
    }
}

/// <summary>
/// Reducing a sequence of polls to the one number the experiment is for: how long the cluster had
/// no leader other than the one that was killed.
/// </summary>
public static class FailoverTimeline
{
    /// <summary>
    /// Elapsed seconds at the first sample showing a leader that is neither absent nor the victim,
    /// or null if that never happened within the watch window.
    ///
    /// Both exclusions matter and the shell version got the first one wrong: a leaderless sample
    /// must not count as a new leader, or the outage reads as an instant recovery.
    /// </summary>
    public static int? FirstNewLeader(IEnumerable<FailoverSample> samples, Guid victim)
    {
        foreach (var sample in samples)
        {
            if (sample.Leader is LeaderState.Held held && held.NodeId != victim)
            {
                return sample.ElapsedSeconds;
            }
        }

        return null;
    }

    /// <summary>
    /// The window during which the assignment set claimed full placement while the cluster had no
    /// leader — the "looks perfectly healthy" interval. Reported separately because it is the part
    /// an operator's dashboard would have missed entirely.
    /// </summary>
    public static int SamplesFullyPlacedWhileLeaderless(IEnumerable<FailoverSample> samples, Guid victim,
        int expectedPlacement)
    {
        return samples.Count(s =>
            s.Placed == expectedPlacement &&
            (s.Leader.IsLeaderless || s.Leader.NodeIdOrNull == victim));
    }
}
