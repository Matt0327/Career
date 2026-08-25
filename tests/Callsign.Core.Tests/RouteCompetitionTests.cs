using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class RouteCompetitionTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly Guid R = new("11112222-3333-4444-5555-666677778888");

    [Fact]
    public void Rivals_AreDeterministic_AndInABelievableBand()
    {
        var a = RouteCompetition.Rivals(Cfg, R);
        var b = RouteCompetition.Rivals(Cfg, R);
        Assert.Equal(a.Count, b.Count);                                   // same route → same rivals every read
        Assert.InRange(a.Count, Cfg.CompetitionMinRivals, Cfg.CompetitionMaxRivals);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Name, b[i].Name);
            Assert.False(string.IsNullOrWhiteSpace(a[i].Name));
            Assert.InRange(a[i].ReputationMilli, 40_000, 90_000);
            Assert.InRange(a[i].FareMultiplierMilli, 900, 1200);
        }
    }

    [Fact]
    public void DominatingOnReputationAndPrice_WinsShare_AndLiftsTheCabin()
    {
        // A flawless name at a discount vs the incumbents → majority share and a load LIFT (bounded).
        var strong = RouteCompetition.Evaluate(Cfg, R, yourRepMilli: 100_000, yourFareMilli: 850);
        // A no-name at a steep premium → minority share and a load DRAG.
        var weak = RouteCompetition.Evaluate(Cfg, R, yourRepMilli: 5_000, yourFareMilli: 1600);

        Assert.True(strong.YourShareMilli > weak.YourShareMilli);
        Assert.True(strong.LoadMultiplier > 1.0);
        Assert.True(weak.LoadMultiplier < 1.0);
    }

    [Fact]
    public void LoadMultiplier_IsBounded_BothWays()
    {
        var crush = RouteCompetition.Evaluate(Cfg, R, 100_000, Cfg.MinScheduledFareMilli); // best case
        var lose = RouteCompetition.Evaluate(Cfg, R, 0, Cfg.MaxScheduledFareMilli);        // worst case
        Assert.True(crush.LoadMultiplier <= 1 + Cfg.CompetitionLoadSwing + 1e-9);
        Assert.True(lose.LoadMultiplier >= 1 - Cfg.CompetitionLoadSwing - 1e-9);
    }

    [Fact]
    public void UnderpricingRivals_WinsShare()
    {
        // Holding reputation equal, a lower fare captures more of the market.
        var cheap = RouteCompetition.Evaluate(Cfg, R, 60_000, 850);
        var dear = RouteCompetition.Evaluate(Cfg, R, 60_000, 1400);
        Assert.True(cheap.YourShareMilli > dear.YourShareMilli);
    }
}
