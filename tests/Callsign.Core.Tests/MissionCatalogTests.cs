using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class MissionCatalogTests
{
    [Fact]
    public void All_AreWellFormed_AndIncludeTheStaples()
    {
        Assert.Contains(MissionCatalog.All, d => d.Type == MissionType.Cargo);
        Assert.Contains(MissionCatalog.All, d => d.Type == MissionType.Passenger);
        Assert.All(MissionCatalog.All, d =>
        {
            Assert.True(d.BoardShare > 0);
            Assert.NotEmpty(d.Labels);
            Assert.True(d.RewardMult > 0);
            Assert.True(d.XpMult > 0);
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
        });
    }

    [Fact]
    public void PremiumTypes_HaveHigherRewardMultiplier_ThanCargo()
    {
        double cargo = MissionCatalog.Def(MissionType.Cargo).RewardMult;
        foreach (var t in new[] { MissionType.Express, MissionType.Sensitive, MissionType.Hazardous, MissionType.Vip })
            Assert.True(MissionCatalog.Def(t).RewardMult > cargo);
    }

    [Fact]
    public void ReputationTypes_ArePresent_WithTheirGatesAndTradeoffs() // Phase 3f
    {
        Assert.True(MissionCatalog.Def(MissionType.Emergency).MinReputationMilli > 0);       // trusted only at high rep
        Assert.True(MissionCatalog.Def(MissionType.SearchAndRescue).MinReputationMilli > 0);
        Assert.True(MissionCatalog.Def(MissionType.Illicit).ReputationMilliReward < 0);      // flying it costs reputation
        Assert.Equal(0, MissionCatalog.Def(MissionType.Illicit).MinReputationMilli);         // but always on offer
        Assert.Null(MissionCatalog.TryDef((MissionType)999));                                // safe lookup for unknowns
    }
}
