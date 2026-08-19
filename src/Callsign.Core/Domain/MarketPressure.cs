namespace Callsign.Core.Domain;

/// <summary>
/// How far a company's own trading has shoved one airport's price for one good off its neutral level
/// (Phase 7g). Buying drains the local supply and pushes the price up; selling floods it and pushes it
/// down. The stored figure is a signed, decayed weight-of-goods accumulator — positive = net bought here
/// (price lifted), negative = net sold here (price softened) — anchored at <see cref="UpdatedAt"/> and
/// mean-reverting toward zero over time, so an arbitrage you work erodes as you work it and heals once you
/// leave. One row per (company, airport, good); the price effect is a pure function of the decayed value.
/// </summary>
public sealed class MarketPressure
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Icao { get; set; } = "";
    public string Good { get; set; } = "";

    /// <summary>Signed net weight (lb) your trades have moved here, already decayed to <see cref="UpdatedAt"/>.
    /// Positive = bought (price up), negative = sold (price down).</summary>
    public long PressureLbs { get; set; }

    /// <summary>When <see cref="PressureLbs"/> was last folded and anchored — decay is measured from here.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
