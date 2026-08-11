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

    /// <summary>Non-null when the job came from a base route (Phase 4); null for freelance.</summary>
    public Guid? SourceRouteId { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LoadByAt { get; set; }

    [NotMapped]
    public decimal Reward => RewardCents / 100m;
}
