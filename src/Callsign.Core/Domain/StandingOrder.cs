namespace Callsign.Core.Domain;

/// <summary>
/// A repeating route a staff pilot flies autonomously in an owned aircraft (Phase 2d). Its reward is
/// economy-frozen at creation (never player-set), and completed trips are reconciled deterministically
/// from elapsed wall-clock on reopen — so the company keeps ticking while the app is closed (§4.8).
/// </summary>
public sealed class StandingOrder : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StaffId { get; set; }
    public Guid AircraftInstanceId { get; set; }

    public string OriginIcao { get; set; } = null!;
    public string DestIcao { get; set; } = null!;
    public double DistanceNm { get; set; }
    public double RoundTripHours { get; set; }       // time for one there-and-back cycle
    public long RewardPerTripCents { get; set; }      // frozen economy price for one delivered round trip
    public string Commodity { get; set; } = "General freight";
    public int WeightLbs { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastReconciledAt { get; set; }  // watermark: trips before this are already booked

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
