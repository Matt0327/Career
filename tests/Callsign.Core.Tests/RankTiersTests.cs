using Callsign.Core.Domain;
using Callsign.Core.Progression;
using Xunit;

namespace Callsign.Core.Tests;

public class RankTiersTests
{
    [Theory]
    [InlineData(0, PilotRank.Trainee)]
    [InlineData(499, PilotRank.Trainee)]
    [InlineData(500, PilotRank.Copilot)]
    [InlineData(1_999, PilotRank.Copilot)]
    [InlineData(2_000, PilotRank.Captain)]
    [InlineData(6_000, PilotRank.SeniorCaptain)]
    [InlineData(15_000, PilotRank.Chief)]
    [InlineData(999_999, PilotRank.Chief)]
    public void ForXp_MapsToTier(int xp, PilotRank expected)
        => Assert.Equal(expected, RankTiers.ForXp(xp));

    [Fact]
    public void All_IsAscendingByMinXp_AndCoversEveryRank()
    {
        var xps = RankTiers.All.Select(t => t.MinXp).ToList();
        Assert.Equal(xps.OrderBy(x => x).ToList(), xps);                        // ascending
        Assert.Equal(Enum.GetValues<PilotRank>().Length, RankTiers.All.Count);  // a tier for every rank
        Assert.All(RankTiers.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Description))); // self-documenting
    }

    [Fact]
    public void Next_WalksUp_AndStopsAtTheTop()
    {
        Assert.Equal(PilotRank.Copilot, RankTiers.Next(PilotRank.Trainee)!.Rank);
        Assert.Equal(PilotRank.Chief, RankTiers.Next(PilotRank.SeniorCaptain)!.Rank);
        Assert.Null(RankTiers.Next(PilotRank.Chief));
    }
}
