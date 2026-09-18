namespace SafetyLab.Cluster;

/// <summary>
/// Who leads, as a type rather than a line of text.
///
/// <see cref="Leaderless"/> is a first-class state, not a parse failure and not an empty string.
/// The shell version answered the single word "none" and every consumer read it with
/// <c>cut -f2</c>, which returns the whole word when there is no tab — so "none" arrived as a node
/// id, compared unequal to the victim, and the script announced a NEW LEADER at t+0 against a
/// cluster that had none. That inverted the measurement: a five-minute outage was reported as an
/// instant recovery. A leaderless cluster is the thing being measured; it cannot be a fallback
/// case.
/// </summary>
public abstract record LeaderState
{
    public sealed record Leaderless : LeaderState;

    public sealed record Held(string Pod, Guid NodeId) : LeaderState;

    public bool IsLeaderless => this is Leaderless;

    /// <summary>
    /// The leader's node id, or null when there is none. Deliberately NOT called NodeId: the
    /// nullable "who leads, if anyone" and Held's non-null "who leads" are different questions,
    /// and collapsing them into one name is how a leaderless state gets read as a node id.
    /// </summary>
    public Guid? NodeIdOrNull => this is Held h ? h.NodeId : null;

    public string Describe() => this is Held h ? $"{h.Pod} ({h.NodeId})" : "none";

    /// <summary>
    /// Parse the output of <c>safetylab query leader</c>: either the single word "none", or
    /// "&lt;pod&gt;\t&lt;node id&gt;". Anything else is leaderless-with-a-reason rather than a
    /// silent default, because "I could not tell" and "there is no leader" are different facts
    /// and only one of them is a result.
    /// </summary>
    public static LeaderState Parse(string raw, out string problem)
    {
        problem = "";
        var text = raw.Trim();

        if (text.Length == 0)
        {
            problem = "empty output from the leader query";
            return new Leaderless();
        }

        if (text == "none") return new Leaderless();

        var parts = text.Split('\t');
        if (parts.Length < 2)
        {
            problem = $"unparseable leader line '{text}' (expected '<pod>\\t<node id>')";
            return new Leaderless();
        }

        if (!Guid.TryParse(parts[1].Trim(), out var nodeId))
        {
            problem = $"leader line '{text}' does not carry a node id";
            return new Leaderless();
        }

        return new Held(parts[0].Trim(), nodeId);
    }
}

/// <summary>
/// The leadership lock itself: who holds it and when it lapses. RavenDB-only — on PostgreSQL a
/// lock has no expiry, because the death of its session is its release.
/// </summary>
public sealed record LockState(string Key, Guid? Holder, DateTimeOffset? ExpiresAt)
{
    public static LockState? Parse(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || text == "none") return null;

        var parts = text.Split('\t');
        if (parts.Length < 3) return null;

        return new LockState(
            parts[0].Trim(),
            Guid.TryParse(parts[1].Trim(), out var holder) ? holder : null,
            DateTimeOffset.TryParse(parts[2].Trim(), out var expires) ? expires : null);
    }

    public double? SecondsToExpiry(DateTimeOffset now)
        => ExpiresAt is { } e ? (e - now).TotalSeconds : null;
}
