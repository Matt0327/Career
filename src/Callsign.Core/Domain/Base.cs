namespace Callsign.Core.Domain;

/// <summary>
/// A company base at an airport (Phase 2e): where your fleet lives, and where you operate from without
/// paying a landing fee. Opening one costs a setup fee and a recurring rent (a ledger debit); the home
/// base is opened free at career start. Server-ready for the future shared world.
/// </summary>
public sealed class Base : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string AirportIcao { get; set; } = null!;
    public bool IsHome { get; set; }
    public long RentPerDayCents { get; set; }
    public int MaintenanceLevel { get; set; }             // 0 = none; a maintenance shop discounts servicing here (Phase 7g)
    public int FuelFarmLevel { get; set; }                // 0 = none; a fuel farm discounts the fuel burned on legs departing here (Phase 7g)
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset LastRentBilledAt { get; set; }  // rent-accrual watermark
    public bool IsActive { get; set; } = true;

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
