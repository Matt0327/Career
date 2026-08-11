using Callsign.Core.Aircraft;
using Xunit;

namespace Callsign.Core.Tests;

public class DefaultFleetCatalogTests
{
    [Fact]
    public void Catalog_IsWellFormed_WithSaneSpecs()
    {
        var all = DefaultFleetCatalog.Aircraft2024;

        Assert.NotEmpty(all);
        Assert.Contains(all, a => a.IcaoTypeDesignator == "C172");
        Assert.Contains(all, a => a.IcaoTypeDesignator == "PC12");

        // ICAO keys are distinct (they are the merge key).
        Assert.Equal(all.Count, all.Select(a => a.IcaoTypeDesignator.ToUpperInvariant()).Distinct().Count());

        Assert.All(all, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.CanonicalName));
            Assert.True(a.Seats > 0);
            Assert.True(a.CruiseKtas > 0);
            Assert.True(a.FuelCapacityLbs > 0);
            Assert.True(a.UsefulLoadLbs > 0);
            Assert.True(a.MinRunwayFt >= 0);
            Assert.NotEmpty(a.Aliases);
        });
    }
}
