using Callsign.Core.Time;

namespace Callsign.Core.Economy;

/// <summary>A commodity's current buy/sell price at one airport.</summary>
public sealed record MarketQuote(string Good, string Name, long BuyCents, long SellCents, int UnitWeightLbs)
{
    /// <summary>The airport's structural tilt for this good (Phase 7g): "export" = it produces it cheap
    /// (buy here), "demand" = it wants it dear (sell here), null = neutral. A fixed, learnable profile.</summary>
    public string? Region { get; init; }
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

    public MarketQuote Quote(string icao, TradeGood g)
    {
        double region = RegionBias(icao, g.Key);                        // fixed structural tilt (Phase 7g)
        long mid = (long)Math.Round(g.BasePriceCents * region * Multiplier(icao, g.Key, Epoch()));
        long buy = (long)Math.Round(mid * (1m + _cfg.TradeSpreadPct), MidpointRounding.AwayFromZero);
        long sell = (long)Math.Round(mid * (1m - _cfg.TradeSpreadPct), MidpointRounding.AwayFromZero);
        string? hint = region <= 1 - _cfg.RegionBiasSwing * 0.5 ? "export"
                     : region >= 1 + _cfg.RegionBiasSwing * 0.5 ? "demand" : null;
        return new MarketQuote(g.Key, g.Name, buy, sell, g.UnitWeightLbs) { Region = hint };
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
