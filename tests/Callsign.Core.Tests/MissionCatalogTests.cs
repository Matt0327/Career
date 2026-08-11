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
    public void ReputationGatedTypes_AreNotOnTheBoardYet() // arrive with 3f
    {
        Assert.DoesNotContain(MissionCatalog.All, d => d.Type == MissionType.Illicit);
        Assert.DoesNotContain(MissionCatalog.All, d => d.Type == MissionType.Emergency);
        Assert.All(MissionCatalog.All, d => Assert.Equal(0, d.MinReputationMilli)); // no reputation gate live yet
    }
}
