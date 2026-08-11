namespace Callsign.Core.Domain;

/// <summary>OurAirports airport classification.</summary>
public enum AirportKind
{
    Unknown,
    Closed,
    Heliport,
    SeaplaneBase,
    Balloonport,
    SmallAirport,
    MediumAirport,
    LargeAirport,
}

/// <summary>
/// A single airport from the bundled OurAirports snapshot (public domain). This is shared
/// reference data — replaced wholesale on update, not per-save merged — so it carries no
/// sync hooks. Landing/parking FEES are deliberately NOT stored here: they are per-airport
/// quotes from the price-provider seam (see docs/domain-notes.md), never a local column.
/// </summary>
public sealed class Airport
{
    /// <summary>OurAirports primary identifier (ICAO for real airports). Natural key.</summary>
    public string Ident { get; set; } = null!;

    public string? IcaoCode { get; set; }
    public string? IataCode { get; set; }
    public AirportKind Kind { get; set; }
    public string Name { get; set; } = null!;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? ElevationFt { get; set; }
    public string? IsoCountry { get; set; }
    public string? IsoRegion { get; set; }
    public string? Municipality { get; set; }
    public bool ScheduledService { get; set; }

    /// <summary>Longest non-closed runway in feet, derived at import. Null when unknown.</summary>
    public int? LongestRunwayFt { get; set; }
}
