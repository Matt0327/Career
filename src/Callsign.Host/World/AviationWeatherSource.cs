using System.Collections.Concurrent;
using System.Net.Http.Json;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;

namespace Callsign.Host.World;

/// <summary>
/// The live METAR adapter (Phase 9b) — the ONLY place a weather network call happens. It reads
/// aviationweather.gov (NOAA AWC: keyless, public-domain JSON, ~hourly obs) and lives in the Host because it
/// owns <c>HttpClient</c> + appsettings, so <c>Callsign.Core</c> keeps no <c>System.Net.Http</c> reference — a
/// project-graph firewall lock (reconcile + TradeService can't reach the feed even by accident). Reads
/// (<see cref="TryObserve"/>) are cache-only and never block; the network runs ONLY in the detached
/// <see cref="Prefetch"/>, hard-timeout-bounded, swallowing every failure so the synthetic model always stands.
/// This is the untested-here branch (the real HTTP round-trip); everything above the <see cref="IWeatherSource"/>
/// boundary is unit-tested against a fake.
/// </summary>
public sealed class AviationWeatherSource : IWeatherSource
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly EconomyConfig _cfg;
    private readonly IClock _clock;
    private readonly ILogger<AviationWeatherSource> _log;
    private readonly ConcurrentDictionary<string, (WeatherReading Reading, DateTimeOffset FetchedAt)> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inflight = new();

    public const string HttpClientName = "liveweather";

    public AviationWeatherSource(IHttpClientFactory httpFactory, EconomyConfig cfg, IClock clock, ILogger<AviationWeatherSource> log)
    {
        _httpFactory = httpFactory;
        _cfg = cfg;
        _clock = clock;
        _log = log;
    }

    // Keyed by a coarse lat/lon cell so a read (which has only coordinates) finds what a prefetch (which also
    // had the ICAO to fetch by) stored — the endpoint always passes the field's exact coordinates.
    private static string CellKey(double lat, double lon) => $"{Math.Round(lat, 2)},{Math.Round(lon, 2)}";

    public WeatherReading? TryObserve(double lat, double lon, DateTimeOffset now)
    {
        if (!_cfg.LiveWeatherEnabled) return null;
        if (!_cache.TryGetValue(CellKey(lat, lon), out var entry)) return null;
        if (now - entry.FetchedAt > _cfg.LiveWeatherTtl) return null;      // the cache entry has aged out
        if (now - entry.Reading.AsOf > _cfg.LiveWeatherMaxAge) return null; // the obs itself is too old to drive mechanics
        return entry.Reading;
    }

    public void Prefetch(double lat, double lon, string? icao)
    {
        if (!_cfg.LiveWeatherEnabled || string.IsNullOrWhiteSpace(icao)) return; // bbox nearest-station lookup deferred
        var key = CellKey(lat, lon);
        if (_cache.TryGetValue(key, out var e) && _clock.UtcNow - e.FetchedAt <= _cfg.LiveWeatherTtl) return; // still fresh
        if (!_inflight.TryAdd(key, 0)) return; // a fetch for this cell is already in flight
        _ = FetchAsync(key, icao!.Trim().ToUpperInvariant());
    }

    private async Task FetchAsync(string cellKey, string icao)
    {
        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var cts = new CancellationTokenSource(_cfg.LiveWeatherTimeout);
            var rows = await http.GetFromJsonAsync<List<MetarJson>>($"metar?ids={icao}&format=json", cts.Token);
            var row = rows?.FirstOrDefault(r => string.Equals(r.IcaoId, icao, StringComparison.OrdinalIgnoreCase)) ?? rows?.FirstOrDefault();
            if (row?.ObsTime is not long obs) return;
            var weather = MetarParser.FromJson(row) ?? MetarParser.FromRaw(row.RawOb);
            if (weather is null) return;
            _cache[cellKey] = (new WeatherReading(weather, IsLive: true, DateTimeOffset.FromUnixTimeSeconds(obs)), _clock.UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live METAR fetch for {Icao} failed — synthetic weather stands.", icao);
        }
        finally
        {
            _inflight.TryRemove(cellKey, out _);
        }
    }
}
