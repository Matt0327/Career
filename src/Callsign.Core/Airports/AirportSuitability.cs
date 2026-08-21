using Callsign.Core.Domain;

namespace Callsign.Core.Airports;

/// <summary>
/// Shared, pure predicates for "can an aircraft/operation actually use this airport" (Phase 12). The pieces —
/// airport kind, a real ICAO ident, the longest runway, and an aircraft type's minimum runway — already existed
/// in the reference data; this is the one place that compares them, so the job board, ferry, and crew/aircraft
/// positioning all judge suitability the same way.
/// </summary>
public static class AirportSuitability
{
    /// <summary>A landplane can operate here at all — a real airport kind (not a heliport / seaplane base / closed field).</summary>
    public static bool IsLandable(Airport a)
        => a.Kind is AirportKind.SmallAirport or AirportKind.MediumAirport or AirportKind.LargeAirport;

    /// <summary>The field has a real ICAO (its Ident IS its ICAO), so it exists in the sim — not an OurAirports placeholder like "NL-0099".</summary>
    public static bool HasRealIcao(Airport a)
        => !string.IsNullOrEmpty(a.IcaoCode) && a.Ident == a.IcaoCode;

    /// <summary>
    /// The runway is long enough for an aircraft needing <paramref name="minRunwayFt"/>. Conservative on missing
    /// data: an unknown requirement (null / ≤0, e.g. a helicopter or an uncatalogued type) needs nothing, and an
    /// unknown runway length is NOT treated as too short — we only ever reject when we can PROVE it's inadequate.
    /// </summary>
    public static bool RunwayAdequate(Airport a, int? minRunwayFt)
        => minRunwayFt is not > 0
        || a.LongestRunwayFt is not int len
        || len >= minRunwayFt.Value;

    /// <summary>A real, landable field with a runway adequate for an aircraft needing <paramref name="minRunwayFt"/>.</summary>
    public static bool IsSuitable(Airport a, int? minRunwayFt = null)
        => IsLandable(a) && HasRealIcao(a) && RunwayAdequate(a, minRunwayFt);
}
