namespace Callsign.Server.Domain;

/// <summary>
/// A player's latest public standing — one row per account, upserted whenever the app submits. Values are
/// self-reported for now (sanity-clamped on the way in); authoritative server-side validation arrives with
/// the shared economy. Keyed by UserId so a player has exactly one entry across every board.
/// </summary>
public sealed class LeaderboardStat
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public long NetWorthCents { get; set; }
    public int Flights { get; set; }
    public int ReputationMilli { get; set; }
    public long Xp { get; set; }
    public string RankKey { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
