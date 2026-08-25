using Callsign.Core.Airline;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class AirlineValuationTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    [Fact]
    public void Valuation_IsNetAssetsPlusGoodwill()
    {
        // Net worth $1M, 50 rep, 2 routes, 1 scheduled, $2M lifetime earnings.
        var r = AirlineValuation.Compute(Cfg, 100_000_000, 50_000, routes: 2, scheduled: 1, lifetimeEarningsCents: 200_000_000);
        long brand = (long)(50.0 * Cfg.AirlineRepValuationCents);
        long network = 2 * Cfg.AirlineRouteValuationCents + 1 * Cfg.AirlineScheduledValuationCents;
        long earnings = (long)(200_000_000 * Cfg.AirlineEarningsValuationFactor);
        Assert.Equal(100_000_000 + brand + network + earnings, r.TotalCents);
        Assert.True(r.TotalCents > 100_000_000); // a running airline is worth more than its net assets
    }

    [Fact]
    public void Valuation_GoodwillGrowsWithReputationAndNetwork()
    {
        var bare = AirlineValuation.Compute(Cfg, 100_000_000, 0, 0, 0, 0);
        var built = AirlineValuation.Compute(Cfg, 100_000_000, 80_000, 5, 3, 500_000_000);
        Assert.Equal(100_000_000, bare.TotalCents);       // no goodwill without an operation
        Assert.True(built.TotalCents > bare.TotalCents);  // brand + network + earnings lift it
    }

    [Fact]
    public void Valuation_DropsZeroLines()
        => Assert.DoesNotContain(AirlineValuation.Compute(Cfg, 100_000_000, 0, 0, 0, 0).Breakdown, l => l.Cents == 0);
}
