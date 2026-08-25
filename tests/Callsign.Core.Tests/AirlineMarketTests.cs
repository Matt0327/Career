using Callsign.Core.Airline;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class AirlineMarketTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default; // 1,000,000 shares

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SharePrice_IsEnterpriseValuePerShare_AndMarketCapEqualsValuation()
    {
        // $50M enterprise value / 1,000,000 shares = $50.00 a share; market cap == the enterprise value (no money minted).
        var r = AirlineMarket.Compute(Cfg, valuationCents: 5_000_000_000, history: new List<(DateTimeOffset, long)>());
        Assert.Equal(5_000, r.SharePriceCents);          // $50.00
        Assert.Equal(5_000_000_000, r.MarketCapCents);   // == valuation
        Assert.Equal(Cfg.AirlineSharesOutstanding, r.SharesOutstanding);
    }

    [Fact]
    public void GrowthSinceFlotation_MeasuresAgainstTheEarliestMark()
    {
        // Floated at $40M, now worth $50M → +25% since flotation; the earliest history row is the baseline.
        var history = new List<(DateTimeOffset, long)>
        {
            (T0, 4_000_000_000),                 // flotation baseline
            (T0.AddDays(1), 4_500_000_000),
        };
        var r = AirlineMarket.Compute(Cfg, valuationCents: 5_000_000_000, history);
        Assert.Equal(0.25, r.GrowthSinceFlotationPct, 3);
        Assert.Equal(4_000, r.FlotationSharePriceCents); // $40.00 at flotation
        Assert.Equal(2, r.History.Count);
        Assert.Equal(4_000, r.History[0].SharePriceCents);
        Assert.Equal(4_500, r.History[1].SharePriceCents);
    }

    [Fact]
    public void NoHistory_ReadsFlat_NotUndefined()
    {
        // A just-incorporated airline with no marks yet: "now" is its own baseline → 0% growth, price defined.
        var r = AirlineMarket.Compute(Cfg, valuationCents: 5_000_000_000, history: new List<(DateTimeOffset, long)>());
        Assert.Equal(0.0, r.GrowthSinceFlotationPct);
        Assert.Equal(r.SharePriceCents, r.FlotationSharePriceCents);
        Assert.Empty(r.History);
    }

    [Fact]
    public void ZeroValuation_IsSafe()
    {
        var r = AirlineMarket.Compute(Cfg, valuationCents: 0, history: new List<(DateTimeOffset, long)>());
        Assert.Equal(0, r.SharePriceCents);
        Assert.Equal(0.0, r.GrowthSinceFlotationPct);
    }
}
