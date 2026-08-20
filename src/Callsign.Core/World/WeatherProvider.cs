using System.Collections.Concurrent;
using Callsign.Core.Economy;

namespace Callsign.Core.World;

/// <summary>
/// The resolver (Phase 9b): the ONLY place the live seam and the synthetic model meet. It sits ABOVE
/// <see cref="WorldOracle"/> — the oracle never learns the word <see cref="IWeatherSource"/> — so the firewall is
/// a property of the dependency graph: nothing the autonomous reconcile loop can reach holds this provider, and
/// the seam's present-tense shape can't answer a past-slot query anyway. Live is consulted on exactly two
/// present-tense surfaces: the display chip (<see cref="Observed"/>) and, behind its own sub-toggle, the market
/// shading (<see cref="MarketWeather"/>). When live weather is OFF the injected source is
/// <see cref="NullWeatherSource"/>, so both fall through to the synthetic floor — byte-identical to Phase 8.
/// </summary>
public sealed class WeatherProvider
{
    private readonly WorldOracle _floor;
    private readonly IWeatherSource _live;
    private readonly EconomyConfig _cfg;
    // (cellLat, cellLon, WeatherWindow-epoch) → the market weather committed for that window. Immutable per
    // window (GetOrAdd), so a quote shown and the settlement seconds later in the same window read the identical
    // value → identical WeatherDemandFactor → exact display==settlement, even as fresh METARs land.
    private readonly ConcurrentDictionary<(long Cx, long Cy, long Epoch), Weather> _marketPins = new();

    public WeatherProvider(WorldOracle floor, IWeatherSource live, EconomyConfig cfg)
    {
        _floor = floor;
        _live = live;
        _cfg = cfg;
    }

    /// <summary>The DISPLAY reading for a field: the freshest live obs if the cache has one, else the synthetic
    /// model at the SAME caller-captured <paramref name="now"/> (the provider never re-reads the clock, so there
    /// is no window-boundary drift between the live and synthetic branches). Also warms the cache for next time.</summary>
    public WeatherReading Observed(double lat, double lon, DateTimeOffset now, string? icao = null)
        => TryLive(lat, lon, now, icao)
           ?? new WeatherReading(_floor.WeatherAt(lat, lon, now), IsLive: false, AsOf: now);

    // Warm the cache (the icao drives the live fetch; the read keys by cell) and read it — guarded, so even a
    // misbehaving source can never break a read or delay a caller: any throw degrades to synthetic (L10).
    private WeatherReading? TryLive(double lat, double lon, DateTimeOffset now, string? icao)
    {
        try
        {
            _live.Prefetch(lat, lon, icao);
            return _live.TryObserve(lat, lon, now);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The MARKET (8f) reading for a field: window-pinned so it can't flip mid-window (exact
    /// display==settlement). Sub-gated by <see cref="EconomyConfig.LiveWeatherFeedsMarket"/> — when off, this is
    /// pure synthetic even while the display chip shows a live reading.</summary>
    public Weather MarketWeather(double lat, double lon, DateTimeOffset now, string? icao = null)
    {
        if (!_cfg.LiveWeatherFeedsMarket)
            return _floor.WeatherAt(lat, lon, now);
        var key = CellEpochKey(lat, lon, now);
        // Commit ONE weather value per (cell, window) via GetOrAdd — a cold first-touch pins synthetic for this
        // window (and warms the cache), so live wins from the next window; the pin can't flip mid-window.
        var pinned = _marketPins.GetOrAdd(key, _ =>
            TryLive(lat, lon, now, icao) is { } r ? r.Weather : _floor.WeatherAt(lat, lon, now));
        PruneOldEpochs(key.Epoch);
        return pinned;
    }

    // Pin PER-FIELD (~0.01° ≈ 1 km) within a window, NOT the synthetic model's coarse WeatherCellDeg (2.5°)
    // regional cell: a live METAR is a single station's observation, so it must not shade every field in a
    // whole cell with one station's weather (the display chip already resolves per-station). display==settlement
    // still holds — the same field in the same window pins exactly once. A synthetic fall-through evaluated at
    // the field's own coordinates is still deterministic.
    private (long Cx, long Cy, long Epoch) CellEpochKey(double lat, double lon, DateTimeOffset now)
    {
        long window = Math.Max(1, _cfg.WeatherWindow.Ticks);
        long epoch = now.UtcTicks / window;
        return ((long)Math.Round(lat * 100), (long)Math.Round(lon * 100), epoch);
    }

    // Keep the pin dictionary bounded: once a new window opens, drop pins two-or-more windows old.
    private void PruneOldEpochs(long currentEpoch)
    {
        foreach (var k in _marketPins.Keys)
            if (k.Epoch < currentEpoch - 1)
                _marketPins.TryRemove(k, out _);
    }
}
