namespace Callsign.Core.Geo;

/// <summary>Great-circle helpers on a spherical Earth — accurate enough for job distances.</summary>
public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;

    /// <summary>Great-circle distance between two lat/lon points, in nautical miles.</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusNm * 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Degrees of latitude spanning a given distance (for bounding-box prefilters).</summary>
    public static double LatDegForNm(double nm) => nm / 60.0;

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
