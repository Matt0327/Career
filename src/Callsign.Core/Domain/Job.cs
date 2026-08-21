using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>
/// A generated job offer. Reward here is a QUOTE — at accept it is frozen onto the assignment so
/// "the number you saw is the number you get" holds even if the board regenerates (domain-notes).
/// Generated content carries <see cref="GeneratedAt"/> + <see cref="ExpiresAt"/>.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public MissionType Type { get; set; }
    public string OriginIcao { get; set; } = null!;
    public string DestIcao { get; set; } = null!;
    public string Commodity { get; set; } = null!;
    public int WeightLbs { get; set; }
    public int Pax { get; set; }
    public double DistanceNm { get; set; }
    public long RewardCents { get; set; }
    public int Xp { get; set; }
    public PilotRank RequiredRank { get; set; }

    /// <summary>The client offering this job (Phase 8d) — a stable per-company key + display name from the
    /// origin's deterministic roster. Null on legacy/route-sourced jobs, which stay anonymous.</summary>
    public string? ClientKey { get; set; }
    public string? ClientName { get; set; }

    /// <summary>Non-null when the job came from a base route (Phase 4); null for freelance.</summary>
    public Guid? SourceRouteId { get; set; }

    /// <summary>Phase 12 — how many cents of this reward your OPERATING REPUTATION added because the board is at
    /// one of your hubs (11c), frozen at posting alongside the reward. Purely for legibility — it's already part
    /// of <see cref="RewardCents"/>; it makes the reputation→money flywheel visible. Null/0 off-hub.</summary>
    public long? HubRepBonusCents { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LoadByAt { get; set; }

    [NotMapped]
    public decimal Reward => RewardCents / 100m;
}
