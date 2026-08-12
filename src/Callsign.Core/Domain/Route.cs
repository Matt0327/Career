namespace Callsign.Core.Domain;

/// <summary>
/// A scheduled, repeatable line between two of your bases (Phase 4d) — the network evolution of a
/// Phase-2d standing order. A named route flies a chosen mission with an owned aircraft + staff pilot,
/// booking trips autonomously in the reconcile pass. Because both ends are your bases, its landings are
/// fee-free. Reward is economy-frozen at creation (you set the constraints, never the price). Syncable.
/// </summary>
public sealed class Route : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = null!;
    public string OriginIcao { get; set; } = null!;   // a base
    public string DestIcao { get; set; } = null!;      // a base
    public MissionType Mission { get; set; }
    public double DistanceNm { get; set; }
    public double RoundTripHours { get; set; }
    public long RewardPerTripCents { get; set; }       // economy-frozen
    public Guid AircraftInstanceId { get; set; }
    public Guid StaffId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastReconciledAt { get; set; } // reconcile watermark

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
