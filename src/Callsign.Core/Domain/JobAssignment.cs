namespace Callsign.Core.Domain;

/// <summary>Lifecycle of an accepted job. Pinned (persisted).</summary>
public enum AssignmentStatus
{
    Accepted = 1,
    Settled = 2,
    Abandoned = 3,
}

/// <summary>
/// A job the player has accepted. It FREEZES a snapshot of the job (reward, XP, weight, distance,
/// route) at accept time, so settlement reads the frozen values and never the live — or removed —
/// Job. That is what makes "the number you saw is the number you get" true by construction
/// (domain-notes, critic #11).
/// </summary>
public sealed class JobAssignment
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PilotId { get; set; }

    // Frozen snapshot of the job at accept:
    public MissionType Type { get; set; }
    public string OriginIcao { get; set; } = null!;
    public string DestIcao { get; set; } = null!;
    public string Commodity { get; set; } = null!;
    public int WeightLbs { get; set; }
    public int Pax { get; set; }
    public double DistanceNm { get; set; }
    public long RewardQuoteCents { get; set; }
    public int XpQuote { get; set; }

    /// <summary>The client this job was offered by (Phase 8d), frozen at accept so settlement credits the
    /// same relationship even if the board has since regenerated. Null for anonymous/legacy jobs.</summary>
    public string? ClientKey { get; set; }
    public string? ClientName { get; set; }

    public AssignmentStatus Status { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>Frozen at accept for time-critical missions (Express, Emergency) — the clock the player
    /// saw is the clock they get (Phase 7d). Null for missions with no delivery deadline.</summary>
    public DateTimeOffset? DeadlineAt { get; set; }
}
