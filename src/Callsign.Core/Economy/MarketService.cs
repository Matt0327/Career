using Callsign.Core.Time;

namespace Callsign.Core.Economy;

/// <summary>A commodity's current buy/sell price at one airport.</summary>
public sealed record MarketQuote(string Good, string Name, long BuyCents, long SellCents, int UnitWeightLbs)
{
    /// <summary>The airport's structural tilt for this good (Phase 7g): "export" = it produces it cheap
    /// (buy here), "demand" = it wants it dear (sell here), null = neutral. A fixed, learnable profile.</summary>
    public string? Region { get; init; }

    /// <summary>How far YOUR own trading has moved this price off neutral (Phase 7g), signed percent:
    /// positive = you've bid it up by buying, negative = you've softened it by selling. Decays back to 0.</summary>
    public int PressurePct { get; init; }

    /// <summary>How far the local WEATHER has lifted this price (Phase 8f), percent: foul conditions
    /// disrupt supply and lift demand, so a storm-hit field pays dearer — 0 in clear weather.</summary>
    public int WeatherPct { get; init; }
}

/// <summary>
/// Deterministic commodity pricing (Phase 2g). A good's mid price is its catalog base swung by a stable
/// hash of (airport, good, time-window): the same good is dear in one place and cheap in another, and a
/// market re-rolls only when the window turns — so a run stays plannable but the map keeps shifting.
/// Buy sits a spread above mid, sell the same below, so profit must come from *where* you sell, not
/// from churning in place. No stored state; prices are a pure function of inputs (server-ready).
/// </summary>
public sealed class MarketService
{
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public MarketService(IClock clock, EconomyConfig cfg)
    {
        _clock = clock;
        _cfg = cfg;
    }

    public IReadOnlyList<MarketQuote> Quotes(string icao)
        => TradeCatalog.Goods.Select(g => Quote(icao, g)).ToList();

    /// <summary>
    /// The live quote for one good at one airport. <paramref name="effectivePressureLbs"/> is the caller's
    /// own already-decayed trading footprint here (0 = untouched market): positive lifts the price, negative
    /// softens it, on top of the fixed region tilt and the time-window swing. <paramref name="weatherDemandFactor"/>
    /// is the local weather's demand lift (Phase 8f; 1.0 = clear/neutral), so callers with no weather data price
    /// the market unchanged.
    /// </summary>
    public MarketQuote Quote(string icao, TradeGood g, long effectivePressureLbs = 0, double weatherDemandFactor = 1.0)
    {
        double region = RegionBias(icao, g.Key);                        // fixed structural tilt (Phase 7g)
        double pressure = PressureFactor(effectivePressureLbs);         // your own footprint (Phase 7g)
        long mid = (long)Math.Round(g.BasePriceCents * region * Multiplier(icao, g.Key, Epoch()) * pressure * weatherDemandFactor);
        long buy = (long)Math.Round(mid * (1m + _cfg.TradeSpreadPct), MidpointRounding.AwayFromZero);
        long sell = (long)Math.Round(mid * (1m - _cfg.TradeSpreadPct), MidpointRounding.AwayFromZero);
        string? hint = region <= 1 - _cfg.RegionBiasSwing * 0.5 ? "export"
                     : region >= 1 + _cfg.RegionBiasSwing * 0.5 ? "demand" : null;
        int pressurePct = (int)Math.Round((pressure - 1) * 100);
        int weatherPct = (int)Math.Round((weatherDemandFactor - 1) * 100);
        return new MarketQuote(g.Key, g.Name, buy, sell, g.UnitWeightLbs) { Region = hint, PressurePct = pressurePct, WeatherPct = weatherPct };
    }

    /// <summary>The price factor from a trading footprint: neutral at 0, ramping linearly to ±<see
    /// cref="EconomyConfig.MarketPressureSwing"/> as net weight reaches ±full-scale, then clamped.</summary>
    private double PressureFactor(long effectivePressureLbs)
    {
        if (effectivePressureLbs == 0 || _cfg.MarketPressureFullScaleLbs <= 0) return 1.0;
        double p = Math.Clamp((double)effectivePressureLbs / _cfg.MarketPressureFullScaleLbs, -1.0, 1.0);
        return 1.0 + p * _cfg.MarketPressureSwing;
    }

    /// <summary>Exponential mean-reversion of a stored pressure to a later moment: half is gone each
    /// half-life. Pure and deterministic (same inputs → same output), so reads never need to persist.</summary>
    public static long DecayPressureLbs(long storedLbs, TimeSpan elapsed, TimeSpan halfLife)
    {
        if (storedLbs == 0 || elapsed <= TimeSpan.Zero) return storedLbs;
        double hl = Math.Max(1, halfLife.Ticks);
        double factor = Math.Pow(0.5, elapsed.Ticks / hl);
        return (long)Math.Round(storedLbs * factor, MidpointRounding.AwayFromZero);
    }

    /// <summary>A stable per-airport export/import tilt for a good — no window term, so it never re-rolls:
    /// the map has a permanent shape you can learn and trade against.</summary>
    private double RegionBias(string icao, string goodKey)
    {
        uint h = Fnv1a($"{icao.ToUpperInvariant()}|{goodKey}|region");
        double unit = (h & 0xFFFFFF) / (double)0x1000000; // [0,1)
        return 1 - _cfg.RegionBiasSwing + unit * (2 * _cfg.RegionBiasSwing);
    }

    /// <summary>The current pricing window index — prices are constant within a window.</summary>
    private long Epoch()
    {
        long window = Math.Max(1, _cfg.TradePriceWindow.Ticks);
        return _clock.UtcNow.UtcTicks / window;
    }

    /// <summary>Stable multiplier in [1-swing, 1+swing] from a hash of (airport, good, window).</summary>
    private double Multiplier(string icao, string goodKey, long epoch)
    {
        uint h = Fnv1a($"{icao.ToUpperInvariant()}|{goodKey}|{epoch}");
        double unit = (h & 0xFFFFFF) / (double)0x1000000; // [0,1)
        return 1 - _cfg.TradePriceSwing + unit * (2 * _cfg.TradePriceSwing);
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        // FNV alone avalanches weakly on a single trailing-char change (consecutive epochs differ only
        // in the last digit), which would leave adjacent windows nearly identically priced. A murmur3
        // fmix finalizer spreads every input bit across all 32 output bits, so windows re-roll cleanly.
        h ^= h >> 16;
        h *= 0x7feb352d;
        h ^= h >> 15;
        h *= 0x846ca68b;
        h ^= h >> 16;
        return h;
    }
}
