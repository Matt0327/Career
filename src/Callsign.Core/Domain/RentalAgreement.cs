namespace Callsign.Core.Domain;

/// <summary>Which rental product an agreement is: a short-term <b>Rental</b> (Phase 9f-1) or a term <b>Lease</b> (9f-2).</summary>
public enum RentalKind { Rental = 1, Lease = 2 }

/// <summary>The lifecycle of a rental/lease agreement.</summary>
public enum RentalStatus { Active = 1, Returned = 2, PurchasedOut = 3, WrittenOff = 4 }

/// <summary>
/// A rental or lease of a NON-owned airframe (Phase 9f). The airframe itself is an ordinary
/// <see cref="AircraftInstance"/> with <see cref="OwnershipKind.Rented"/>; this agreement holds everything
/// that makes it a rental — the pickup condition/hours watermark the return bill measures against, the escrow
/// deposit that caps the renter's liability, the frozen rates, and the term. The FULL entity ships in 9f-1;
/// the lease-only fields (<see cref="WeeklyRateCents"/>, <see cref="InsuranceWeeklyCents"/>,
/// <see cref="RentCreditedCents"/>, <see cref="BuyoutCents"/>) stay 0/null for a Rental and are populated by
/// 9f-2 with no schema change.
/// </summary>
public sealed class RentalAgreement : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }            // renter / lessee
    public Guid AircraftInstanceId { get; set; }   // the Rented tail this agreement holds
    public RentalKind Kind { get; set; }
    public RentalStatus Status { get; set; } = RentalStatus.Active;

    public double HoursAtPickup { get; set; }      // airframe hours at pickup — the usage + return baseline
    public int HullMilliAtPickup { get; set; }     // condition watermarks the return-damage bill measures against —
    public int EngineMilliAtPickup { get; set; }   // on the agreement, so a mid-term maintain can't erase the baseline

    public long DepositCents { get; set; }         // escrowed at pickup; caps the renter's abnormal-damage liability
    public long HoldingPerDayCents { get; set; }   // frozen per-day holding fee (Rental)
    public long FlightHourRateCents { get; set; }  // frozen per-flight-hour usage rent (Rental)
    public long WeeklyRateCents { get; set; }      // frozen weekly lease payment (Lease; 0 for Rental)
    public long InsuranceWeeklyCents { get; set; } // lessee-carried hull cover (Lease; 0 for Rental)

    public DateTimeOffset LastRentBilledAt { get; set; } // holding/weekly fee watermark
    public double HoursLastBilled { get; set; }          // usage-rent watermark (airframe hours already billed)
    public long RentCreditedCents { get; set; }          // rent applied toward a buyout (Lease)
    public long? BuyoutCents { get; set; }               // lease strike, recomputed server-side at exercise (null for Rental)
    public int SloppyEventCount { get; set; }            // coaching-band flags logged in term (reserved for the 9f clean-return tick)

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }        // scheduled auto-return

    // Sync hooks (dormant until the shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
