using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class HaloAndRentabilityTests
{
    private static AircraftType Type(string key, AircraftCategory cat, int seats) => new()
    {
        Id = Guid.NewGuid(), Key = key, IcaoTypeDesignator = key, CanonicalName = key,
        Category = cat, Seats = seats, UsefulLoadLbs = 800, CruiseKtas = 150,
    };

    [Fact]
    public void Concorde_IsAHaloPricedAtFiveHundredMillion()
    {
        Assert.True(AircraftPricing.IsHalo("CONC"));
        var conc = Type("CONC", AircraftCategory.Heavy, 100);
        var q = AircraftPricing.Quote(EconomyConfig.Default, conc);
        Assert.Equal(50_000_000_000, q.TotalCents); // $500,000,000
    }

    [Fact]
    public void Concorde_IsInTheCuratedWhitelist()
        => Assert.Contains("CONC", Callsign.Core.Aircraft.DefaultFleetCatalog.IcaoKeys);

    [Fact]
    public void Halo_AndAirliners_AndJets_AreNotRentable()
    {
        Assert.False(AircraftDealerService.IsRentable(Type("CONC", AircraftCategory.Heavy, 100))); // flagship
        Assert.False(AircraftDealerService.IsRentable(Type("B738", AircraftCategory.Heavy, 189))); // airliner
        Assert.False(AircraftDealerService.IsRentable(Type("CRJ7", AircraftCategory.Jet, 70)));     // regional jet
        Assert.False(AircraftDealerService.IsRentable(Type("SF50", AircraftCategory.LightJet, 5))); // light jet
        Assert.False(AircraftDealerService.IsRentable(Type("AT76", AircraftCategory.Turboprop, 72))); // 72-seat regional
    }

    [Fact]
    public void WorkingPropsAndHelis_AreRentable()
    {
        Assert.True(AircraftDealerService.IsRentable(Type("C172", AircraftCategory.LightSingle, 4)));
        Assert.True(AircraftDealerService.IsRentable(Type("BE58", AircraftCategory.LightTwin, 6)));
        Assert.True(AircraftDealerService.IsRentable(Type("C208", AircraftCategory.Turboprop, 9)));
        Assert.True(AircraftDealerService.IsRentable(Type("B06", AircraftCategory.Helicopter, 5)));
    }
}
