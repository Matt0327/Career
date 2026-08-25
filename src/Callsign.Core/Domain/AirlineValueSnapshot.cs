namespace Callsign.Core.Domain;

/// <summary>
/// A periodic mark of the incorporated airline's enterprise value (Phase 13 — "The Flotation"). Recorded at
/// most once per interval when the Airline HQ is read, it builds a value HISTORY as the player operates — the
/// series behind the HQ's share-price ticker and its "since flotation" growth. The earliest row is the flotation
/// baseline. Purely a read-model trail: money-neutral, never touches the ledger, gates nothing.
/// </summary>
public sealed class AirlineValueSnapshot
{
    public long Id { get; set; }            // autoincrement local order key
    public Guid CompanyId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public long ValuationCents { get; set; } // enterprise value at this mark
}
