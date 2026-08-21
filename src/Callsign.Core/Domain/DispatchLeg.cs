namespace Callsign.Core.Domain;

/// <summary>Where a dispatched leg is in its short life.</summary>
public enum DispatchStatus { Flying = 1, Flown = 2 }

/// <summary>
/// A one-off board JOB handed to a hired crew to fly autonomously as a ONE-WAY leg (Phase 12 crew dispatch).
/// Unlike a <see cref="StandingOrder"/> / <see cref="Route"/> — which repeat and always return the tail to its
/// origin — a dispatched leg fires ONCE, and on completion it relocates BOTH the aircraft and its crew to the
/// destination. Its reward is frozen off the board job at dispatch (never re-priced), and it settles through the
/// SAME reconcile machinery as the other autonomous work (RollTrips outcome, ledger, crew-convergence reputation),
/// so it deepens the existing autonomous loop rather than adding a second income path (L11).
///
/// A rented tail MAY fly a dispatched leg (unlike the perpetual paths, which are owned-only): a job is finite and
/// board-sourced, and the leg accrues airframe hours so the rental's per-hour usage fee bills — making a rental
/// leg strictly less profitable than the same leg in an owned tail, which closes the rent-a-tail-for-income pump.
/// </summary>
public sealed class DispatchLeg : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StaffId { get; set; }
    public Guid AircraftInstanceId { get; set; }
    public Guid JobId { get; set; }               // the board job this came from (reference/audit)

    public MissionType Type { get; set; }
    public string OriginIcao { get; set; } = null!;
    public string DestIcao { get; set; } = null!;
    public string Commodity { get; set; } = "";
    public int WeightLbs { get; set; }
    public int Pax { get; set; }
    public double DistanceNm { get; set; }
    public double OneWayHours { get; set; }       // dist / cruise — a single leg, not a round trip
    public long RewardCents { get; set; }         // frozen board pay at dispatch (hub lift stripped for a rental)
    public string? ClientKey { get; set; }
    public string? ClientName { get; set; }

    public DispatchStatus Status { get; set; } = DispatchStatus.Flying;
    public DateTimeOffset DispatchedAt { get; set; }
    /// <summary>When the leg completes — <c>DispatchedAt</c> + the duty-scaled flight time. The reconcile books it
    /// once this is reached, so the FTL duty cap is baked in (a leg takes 24/MaxDutyHoursPerDay × its flight hours
    /// of wall-clock to complete).</summary>
    public DateTimeOffset ReadyAt { get; set; }
    public DateTimeOffset? FlownAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
