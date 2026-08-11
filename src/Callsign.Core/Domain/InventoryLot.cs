namespace Callsign.Core.Domain;

/// <summary>
/// A holding of a tradeable commodity — bought at one airport to sell dearer at another (Phase 2g).
/// Each lot carries its COST BASIS (<see cref="UnitCostCents"/>, the weighted-average price paid) so
/// realised profit on a sale is honest rather than guessed. Goods ride with the pilot: on a settled
/// leg, lots at the departure airport move to the destination. Server-ready for the shared world.
/// </summary>
public sealed class InventoryLot : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Commodity key from the trade catalog (e.g. "coffee").</summary>
    public string Good { get; set; } = null!;

    public int Quantity { get; set; }

    /// <summary>Cost basis: the weighted-average price paid per unit, in cents.</summary>
    public long UnitCostCents { get; set; }

    /// <summary>Where the goods physically are — you can only sell what's at your current airport.</summary>
    public string LocationIcao { get; set; } = null!;

    public DateTimeOffset AcquiredAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
