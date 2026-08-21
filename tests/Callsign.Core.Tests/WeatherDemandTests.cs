using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 8f — foul weather lifts a field's commodity prices (the demand factor + its market wiring).</summary>
public class WeatherDemandTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    [Fact]
    public void WeatherDemandFactor_NeutralInClear_LiftsInFoul_Monotonic()
    {
        Assert.Equal(1.0, Cfg.WeatherDemandFactor(10), 6);                              // clear air lifts nothing
        Assert.Equal(1.0, Cfg.WeatherDemandFactor(Cfg.WeatherDemandClearVisSm), 6);     // at the clear threshold
        Assert.True(Cfg.WeatherDemandFactor(0.4) > 1.0);                                // fog lifts the price
        Assert.Equal(1.0 + Cfg.WeatherDemandSwing, Cfg.WeatherDemandFactor(0), 6);      // zero visibility = the full lift
        Assert.True(Cfg.WeatherDemandFactor(1) > Cfg.WeatherDemandFactor(4));           // fouler = dearer (monotonic)
    }

    [Fact]
    public void WeatherSwing_StaysWithinRoundTripSpread_SoNoSameFieldPump()
    {
        // The weather lift is one-sided (only ever raises the price) and foul weather recurs, so if the swing
        // exceeded the round-trip dealer cost a patient trader could buy in clear air, hold, and sell once a
        // storm rolled in for free profit. The foulest sell must not beat a neutral buy at the same field.
        double foulSell = Cfg.WeatherDemandFactor(0) * (1 - (double)Cfg.TradeSpreadPct); // sell at the maximum lift
        double clearBuy = 1.0 * (1 + (double)Cfg.TradeSpreadPct);                        // buy with weather neutral
        Assert.True(foulSell <= clearBuy, "WeatherDemandSwing must stay <= 2 x TradeSpreadPct");
    }

    [Fact]
    public void WeatherScrubsTrip_FogAndGaleScrub_ClearAndSnowFly()
    {
        Assert.True(Cfg.WeatherScrubsTrip(0.4, 3));    // fog — too little visibility
        Assert.True(Cfg.WeatherScrubsTrip(10, 40));    // gale — too much wind
        Assert.False(Cfg.WeatherScrubsTrip(10, 5));    // clear and calm — flies
        Assert.False(Cfg.WeatherScrubsTrip(1.5, 10));  // snow (1.5 sm) is reduced but flyable
    }

    [Fact]
    public void PressureAndWeather_StackedLift_StillLosesSameFieldRoundTrip_NoPump()
    {
        // Regression (audit H1): the mid is base·region·window·pressure·weather. Each of pressure and weather
        // alone stays within the 2×-spread budget, but they MULTIPLY — stacked (your own buying bids the price
        // up AND foul weather lifts demand) they once broke the same-field-round-trip-loses invariant: buy in
        // clear air at neutral pressure, then sell back in fog with your own positive pressure could profit.
        // Quote now caps the COMBINED upward lift at 1 + 2·TradeSpreadPct, so the worst-case round trip loses.
        var market = new MarketService(new FakeClock(), Cfg);
        var g = TradeCatalog.Goods[0];

        var buy = market.Quote("EHAM", g);                                     // neutral pressure, clear weather
        double foulWeather = Cfg.WeatherDemandFactor(0);                        // 1 + WeatherDemandSwing (zero visibility)
        var sell = market.Quote("EHAM", g, Cfg.MarketPressureFullScaleLbs, foulWeather); // full positive pressure + foulest weather

        Assert.True(sell.SellCents <= buy.BuyCents,
            $"stacked pressure+weather same-field round trip must LOSE: sell {sell.SellCents} vs buy {buy.BuyCents}");
    }

    [Fact]
    public void Quote_WeatherFactor_ScalesTheMid_AndReportsThePct()
    {
        var clock = new FakeClock();
        var market = new MarketService(clock, Cfg);
        var g = TradeCatalog.Goods[0];

        var neutral = market.Quote("EHAM", g);            // default factor 1.0
        var lifted = market.Quote("EHAM", g, 0, 1.10);    // +10% weather demand, same icao/window/region

        Assert.True(lifted.BuyCents > neutral.BuyCents);
        Assert.True(lifted.SellCents > neutral.SellCents);
        Assert.Equal(10, lifted.WeatherPct);
        Assert.Equal(0, neutral.WeatherPct);
        // It scales the mid, so buy tracks ~×1.10 (allow ±1 cent of rounding on the spread).
        Assert.InRange(lifted.BuyCents - (long)System.Math.Round(neutral.BuyCents * 1.10), -1, 1);
    }
}
