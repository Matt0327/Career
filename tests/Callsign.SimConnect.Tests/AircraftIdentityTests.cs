using Callsign.SimConnect;
using Xunit;

namespace Callsign.SimConnect.Tests;

public class AircraftIdentityTests
{
    [Theory]
    [InlineData("Cessna 172 Skyhawk (G1000)", "cessna 172 skyhawk g1000")]
    [InlineData("  ASOBO  C-172   ", "asobo c 172")]
    [InlineData("TBM_930!!", "tbm 930")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsNoiseAndLowercases(string? raw, string expected)
        => Assert.Equal(expected, AircraftIdentity.Normalize(raw));

    [Fact]
    public void Matches_FindsTypeDespiteLiveryNoise()
    {
        string[] aliases = ["Cessna 172"];
        Assert.True(AircraftIdentity.Matches("Cessna 172 Skyhawk | Blue Livery", aliases));
        Assert.True(AircraftIdentity.Matches("ASOBO Cessna-172", aliases));
    }

    [Fact]
    public void Matches_RejectsDifferentAircraft()
    {
        string[] aliases = ["Cessna 172"];
        Assert.False(AircraftIdentity.Matches("Daher TBM 930", aliases));
    }

    [Fact]
    public void Matches_EmptyObservedTitle_IsFalse()
        => Assert.False(AircraftIdentity.Matches("", ["Cessna 172"]));
}
