namespace Callsign.Core.Domain;

/// <summary>How an airframe is held.</summary>
public enum OwnershipKind { Owned = 1, Rented = 2, Listed = 3 }

/// <summary>What an airframe is currently doing (single-occupancy guard, domain-notes §4.4).</summary>
public enum AircraftAvailability { Available = 1, Reserved = 2, InFlight = 3, Grounded = 4 }

/// <summary>
/// One physical, owned airframe (a specific tail) — distinct from the shared <see cref="AircraftType"/>
/// and from machine-local install state. Phase 2a covers ownership; condition, hours, rental terms, and
/// crew fill in later phases (domain-notes §4.4). Syncable for the future shared world.
/// </summary>
public sealed class AircraftInstance : ISyncable
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid? CompanyId { get; set; }     // owner; null while an unowned market listing
    public string Tail { get; set; } = null!;
    public OwnershipKind Ownership { get; set; } = OwnershipKind.Owned;
    public AircraftAvailability Availability { get; set; } = AircraftAvailability.Available;
    public string LocationIcao { get; set; } = null!; // where it sits when not in flight

    public int HullConditionMilli { get; set; } = 100_000;   // 0..100000; full at purchase
    public int EngineConditionMilli { get; set; } = 100_000;
    public double AirframeHours { get; set; }
    public double MaintenanceHoursWatermark { get; set; }    // airframe hours at the last maintenance (§6.6)
    public long? PurchasePriceCents { get; set; }
    public DateTimeOffset? AcquiredAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
