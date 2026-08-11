using Callsign.Core.Geo;
using Xunit;

namespace Callsign.Core.Tests;

public class GeoTests
{
    [Fact]
    public void DistanceNm_SamePoint_IsZero()
        => Assert.Equal(0, GeoMath.DistanceNm(52.3, 4.76, 52.3, 4.76), 3);

    [Fact]
    public void DistanceNm_OneDegreeOfLatitude_IsAboutSixtyNm()
        => Assert.InRange(GeoMath.DistanceNm(0, 0, 1, 0), 59.5, 60.5);
}
