using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Airports;

/// <summary>Read access to the bundled airport reference data.</summary>
public sealed class AirportRepository
{
    private readonly CallsignDbContext _db;

    public AirportRepository(CallsignDbContext db) => _db = db;

    public Task<Airport?> GetByIdentAsync(string ident, CancellationToken ct = default)
        => _db.Airports.FirstOrDefaultAsync(a => a.Ident == ident, ct);

    public async Task<IReadOnlyList<Runway>> GetRunwaysAsync(string ident, CancellationToken ct = default)
        => await _db.Runways.Where(r => r.AirportIdent == ident).ToListAsync(ct);

    /// <summary>
    /// Airports within <paramref name="radiusNm"/> of a point, nearest first. A lat/lon bounding
    /// box (indexable) prefilters candidates; exact great-circle distance then filters and orders.
    /// </summary>
    public async Task<IReadOnlyList<(Airport Airport, double DistanceNm)>> WithinRadiusAsync(
        double lat, double lon, double radiusNm, CancellationToken ct = default)
    {
        double dLat = GeoMath.LatDegForNm(radiusNm);
        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        double dLon = dLat / Math.Max(0.05, Math.Abs(cosLat)); // widen with latitude; guard the poles
        double latMin = lat - dLat, latMax = lat + dLat;
        double lonMin = lon - dLon, lonMax = lon + dLon;

        var candidates = await _db.Airports
            .Where(a => a.Latitude >= latMin && a.Latitude <= latMax
                     && a.Longitude >= lonMin && a.Longitude <= lonMax)
            .ToListAsync(ct);

        return candidates
            .Select(a => (Airport: a, DistanceNm: GeoMath.DistanceNm(lat, lon, a.Latitude, a.Longitude)))
            .Where(x => x.DistanceNm <= radiusNm)
            .OrderBy(x => x.DistanceNm)
            .ToList();
    }

    /// <summary>
    /// The nearest real, landable airport with a runway adequate for an aircraft needing
    /// <paramref name="minRunwayFt"/> (Phase 12). Used to place a freed pilot/aircraft at a genuine field
    /// rather than nowhere. Returns null if nothing suitable is within <paramref name="radiusNm"/>.
    /// </summary>
    public async Task<(Airport Airport, double DistanceNm)?> NearestSuitableAsync(
        double lat, double lon, int? minRunwayFt = null, double radiusNm = 300, CancellationToken ct = default)
    {
        foreach (var candidate in await WithinRadiusAsync(lat, lon, radiusNm, ct))
            if (AirportSuitability.IsSuitable(candidate.Airport, minRunwayFt))
                return candidate;
        return null;
    }
}
