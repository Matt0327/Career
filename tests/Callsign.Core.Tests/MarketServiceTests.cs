using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class MarketServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static MarketService Market(FakeClock clock) => new(clock, Cfg);
    private static TradeGood Coffee => TradeCatalog.Find("coffee")!;

    [Fact]
    public void Quote_IsDeterministic_ForSameAirportGoodWindow()
    {
        var clock = new FakeClock();
        Assert.Equal(Market(clock).Quote("EHAM", Coffee), Market(clock).Quote("EHAM", Coffee));
    }

    [Fact]
    public void Buy_IsAbove_Sell_BySpread()
    {
        var q = Market(new FakeClock()).Quote("EHAM", Coffee);
        Assert.True(q.BuyCents > q.SellCents); // a same-airport round trip loses the spread
    }

    [Fact]
    public void Prices_Vary_AcrossAirports_SoArbitrageExists()
    {
        var m = Market(new FakeClock());
        string[] icaos = ["EHAM", "EGLL", "KJFK", "LFPG", "EDDF", "LEMD", "LIRF", "EIDW"];
        var buys = icaos.Select(i => m.Quote(i, Coffee).BuyCents).ToList();
        var sells = icaos.Select(i => m.Quote(i, Coffee).SellCents).ToList();
        Assert.True(buys.Distinct().Count() > 1);   // not a flat map
        Assert.True(sells.Max() > buys.Min());       // buy cheap here, sell dear there
    }

    [Fact]
    public void Prices_StayConstant_WithinAWindow_ButReRoll_Across()
    {
        var start = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        var sameWindow = new FakeClock { UtcNow = start.AddHours(1) };   // still the first 6h window
        var nextWindow = new FakeClock { UtcNow = start.AddHours(12) };  // two windows on
        var baseClock = new FakeClock { UtcNow = start };

        Assert.Equal(Market(baseClock).Quote("EHAM", Coffee), Market(sameWindow).Quote("EHAM", Coffee));

        string[] icaos = ["EHAM", "EGLL", "KJFK", "LFPG"];
        var before = icaos.Select(i => Market(baseClock).Quote(i, Coffee).BuyCents).ToList();
        var after = icaos.Select(i => Market(nextWindow).Quote(i, Coffee).BuyCents).ToList();
        Assert.NotEqual(before, after);
    }
}
