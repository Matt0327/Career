using Callsign.Core.Economy;

namespace Callsign.Core.World;

/// <summary>The weather at a place and moment: wind, visibility, ceiling, temperature and a headline condition.</summary>
public sealed record Weather(
    int WindDirDeg, int WindKts, int GustKts, double VisibilitySm, int CeilingFt, int TempC, string Condition, string Summary);

/// <summary>Where the macro economy sits (Phase 8c): a demand multiplier on what clients pay for work, and a
/// human label for the phase of the boom↔bust cycle.</summary>
public sealed record EconomyPhase(double DemandMult, string Label);

/// <summary>
/// The world oracle (Phase 8): what the world is like at a place and time, as a deterministic PURE FUNCTION of
/// (coordinates, instant) — no stored state, computed from a stable hash exactly like <see cref="MarketService"/>
/// prices a good. So two clients (or a headless reconcile) compute the identical world for the same moment, and
/// there is nothing to migrate or sync. This is the synthetic model: it drives forecasts and autonomous ops; a
/// player-flown leg reads the sim's ACTUAL conditions for scoring (Phase-8 law L7).
/// </summary>
public sealed class WorldOracle
{
    private readonly EconomyConfig _cfg;

    public WorldOracle(EconomyConfig cfg) => _cfg = cfg;

    /// <summary>The synthetic weather at a latitude/longitude for a moment. Deterministic and reproducible.</summary>
    public Weather WeatherAt(double lat, double lon, DateTimeOffset instant)
    {
        // A weather cell (coarse lat/lon) shared by nearby fields — a regional front, not per-airport noise —
        // that re-rolls once per window, so weather is stable within a run but the map keeps evolving.
        long window = Math.Max(1, _cfg.WeatherWindow.Ticks);
        long epoch = instant.UtcTicks / window;
        int cellLat = (int)Math.Floor(lat / _cfg.WeatherCellDeg);
        int cellLon = (int)Math.Floor(lon / _cfg.WeatherCellDeg);
        uint h = Fnv1a($"{cellLat}|{cellLon}|{epoch}|weather");
        double U(uint salt) => Unit(Fmix(h ^ salt));
        double u1 = U(0x1), u2 = U(0x2), u3 = U(0x3), u4 = U(0x4), u5 = U(0x5);

        // ── temperature: an annual-mean by latitude, a hemisphere-correct seasonal swing, and a diurnal cycle ──
        var utc = instant.UtcDateTime;
        double latAbs = Math.Min(90, Math.Abs(lat));
        double baseTemp = 27 - latAbs * 0.5;                              // ~27C equator .. ~-18C pole (annual mean)
        double peakDay = lat >= 0 ? 172 : 355;                           // N summer solstice ~Jun 21; S flipped
        double seasonPhase = Math.Cos((utc.DayOfYear - peakDay) / 365.25 * 2 * Math.PI); // +1 local summer .. -1 winter
        double seasonalSwing = (3 + latAbs / 90.0 * 17) * seasonPhase;   // small near the equator, large toward the poles
        double localHour = ((utc.TimeOfDay.TotalHours + lon / 15.0) % 24 + 24) % 24; // solar-ish local time from longitude
        double diurnal = 6 * Math.Sin((localHour - 9) / 24.0 * 2 * Math.PI);         // warmest ~15:00, coolest ~03:00
        int tempC = (int)Math.Round(Math.Clamp(baseTemp + seasonalSwing + diurnal + (u1 - 0.5) * 7, -60, 55));

        // ── wind: a direction, and a speed that's calm most days with an occasional gale (a fat tail) ──
        int windDir = (int)(u2 * 360) % 360;
        int windKts = (int)Math.Round(_cfg.WeatherBaseWindKts + Math.Pow(u3, _cfg.WeatherWindTailExp) * _cfg.WeatherMaxWindKts);
        int gustKts = windKts + (int)Math.Round(u4 * Math.Max(2, windKts * 0.4));

        // ── condition / visibility / ceiling: a weighted roll, then physics tweaks (freezing → snow, wind clears fog) ──
        bool freezing = tempC <= 0;
        string cond; double visSm; int ceilFt;
        if (u5 < 0.50) { cond = "Clear"; visSm = 10; ceilFt = 25_000; }
        else if (u5 < 0.72) { cond = "Cloudy"; visSm = 9; ceilFt = 5_000; }
        else if (u5 < 0.86) { cond = freezing ? "Snow" : "Rain"; visSm = freezing ? 1.5 : 4; ceilFt = freezing ? 900 : 2_500; }
        else if (u5 < 0.94) { cond = "Fog"; visSm = 0.4; ceilFt = 200; }
        else { cond = "Storm"; visSm = 2; ceilFt = 1_200; }
        if (cond == "Fog" && windKts >= 12) { cond = "Cloudy"; visSm = 8; ceilFt = 4_000; } // a stiff breeze blows fog off

        visSm = Math.Round(visSm, 1);
        string summary = $"{cond} · {windKts} kt @ {windDir:000}° · {visSm:0.#} sm · {tempC}°C";
        return new Weather(windDir, windKts, gustKts, visSm, ceilFt, tempC, cond, summary);
    }

    /// <summary>The macro economy at a moment (Phase 8c): a slow sine boom↔bust, deterministic in the instant.
    /// The level (sine) sets how much clients pay; the trend (cosine) separates recovery from a downturn.</summary>
    public EconomyPhase EconomyPhaseAt(DateTimeOffset instant)
    {
        long period = Math.Max(1, _cfg.EconomyCyclePeriod.Ticks);
        double frac = (instant.UtcTicks % period) / (double)period;   // [0,1) through the cycle — bounded, precise
        double theta = 2 * Math.PI * frac;
        double level = Math.Sin(theta), trend = Math.Cos(theta);
        double mult = 1 + level * _cfg.EconomyCycleAmplitude;
        string label = level > 0.5 ? "Boom" : level < -0.5 ? "Bust" : trend >= 0 ? "Recovery" : "Slowing";
        return new EconomyPhase(mult, label);
    }

    // FNV-1a with a murmur3 fmix finalizer — the same stable, well-avalanching hash the market uses.
    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return Fmix(h);
    }

    private static uint Fmix(uint h)
    {
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h;
    }

    private static double Unit(uint h) => (h & 0xFFFFFF) / (double)0x1000000; // [0,1)
}
