using Callsign.Core.Economy;
using Callsign.Core.World;
using Xunit;

namespace Callsign.Core.Tests;

public class WorldOracleTests
{
    private static readonly WorldOracle Oracle = new(EconomyConfig.Default);
    private static readonly DateTimeOffset T = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WeatherAt_IsDeterministic()
    {
        var a = Oracle.WeatherAt(52.31, 4.76, T);
        var b = Oracle.WeatherAt(52.31, 4.76, T);
        Assert.Equal(a, b); // pure function of (place, time) — same inputs, byte-identical weather
    }

    [Fact]
    public void WeatherAt_NearbyFieldsShareTheFront()
    {
        // Two fields inside one weather cell (2.5°) get the same regional weather — a front, not per-field noise.
        var a = Oracle.WeatherAt(52.10, 4.10, T);
        var b = Oracle.WeatherAt(52.20, 4.20, T);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WeatherAt_ReRollsOverTime()
    {
        // Across many windows at one field, the weather is not frozen — conditions evolve.
        var seen = new HashSet<string>();
        for (int i = 0; i < 20; i++)
            seen.Add(Oracle.WeatherAt(40.0, -74.0, T.AddHours(i * 4)).Summary);
        Assert.True(seen.Count > 3); // it changes, not a single stuck forecast
    }

    [Fact]
    public void WeatherAt_EquatorIsWarmerThanThePole()
    {
        var equator = Oracle.WeatherAt(0, 10, T);
        var pole = Oracle.WeatherAt(85, 10, T);
        Assert.True(equator.TempC > pole.TempC + 20); // latitude drives a real temperature gradient
    }

    [Fact]
    public void WeatherAt_NorthernSummerIsWarmerThanNorthernWinter()
    {
        var summer = Oracle.WeatherAt(55, 0, new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var winter = Oracle.WeatherAt(55, 0, new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        Assert.True(summer.TempC > winter.TempC); // hemisphere-correct seasonal swing
    }

    [Fact]
    public void WeatherAt_AllFieldsInSaneRanges()
    {
        var cfg = EconomyConfig.Default;
        var conditions = new HashSet<string> { "Clear", "Cloudy", "Rain", "Snow", "Fog", "Storm" };
        for (int i = 0; i < 500; i++)
        {
            var w = Oracle.WeatherAt(30 + i * 0.11, -100 + i * 0.37, T.AddHours(i));
            Assert.InRange(w.WindKts, cfg.WeatherBaseWindKts, cfg.WeatherBaseWindKts + cfg.WeatherMaxWindKts);
            Assert.True(w.GustKts >= w.WindKts);
            Assert.InRange(w.WindDirDeg, 0, 359);
            Assert.True(w.VisibilitySm > 0);
            Assert.True(w.CeilingFt > 0);
            Assert.InRange(w.TempC, -60, 55);
            Assert.Contains(w.Condition, conditions);
            // A snowy field is at or below freezing; a rainy one is above.
            if (w.Condition == "Snow") Assert.True(w.TempC <= 0);
            if (w.Condition == "Rain") Assert.True(w.TempC > 0);
        }
    }

    // ── economy cycle (Phase 8c) ──────────────────────────────────────────────────────────────

    [Fact]
    public void EconomyPhaseAt_IsDeterministic()
    {
        Assert.Equal(Oracle.EconomyPhaseAt(T), Oracle.EconomyPhaseAt(T)); // pure function of the instant
    }

    [Fact]
    public void EconomyPhaseAt_MultiplierStaysWithinAmplitude()
    {
        var cfg = EconomyConfig.Default;
        // Sweep a whole cycle in fine steps; the demand multiplier must never leave [1-amp, 1+amp].
        long step = cfg.EconomyCyclePeriod.Ticks / 500;
        for (int i = 0; i < 1000; i++)
        {
            double m = Oracle.EconomyPhaseAt(T.AddTicks(step * i)).DemandMult;
            Assert.InRange(m, 1 - cfg.EconomyCycleAmplitude - 1e-9, 1 + cfg.EconomyCycleAmplitude + 1e-9);
        }
    }

    [Fact]
    public void EconomyPhaseAt_OscillatesAboveAndBelowPar_OverACycle()
    {
        var cfg = EconomyConfig.Default;
        double min = double.MaxValue, max = double.MinValue;
        long step = cfg.EconomyCyclePeriod.Ticks / 200;
        for (int i = 0; i < 200; i++)
        {
            double m = Oracle.EconomyPhaseAt(T.AddTicks(step * i)).DemandMult;
            min = Math.Min(min, m); max = Math.Max(max, m);
        }
        Assert.True(max > 1.05, "a boom lifts pay well above par");
        Assert.True(min < 0.95, "a bust cuts pay well below par");
    }

    [Fact]
    public void EconomyPhaseAt_LabelsAreTheFourKnownPhases()
    {
        var cfg = EconomyConfig.Default;
        var labels = new HashSet<string>();
        long step = cfg.EconomyCyclePeriod.Ticks / 400;
        for (int i = 0; i < 400; i++)
            labels.Add(Oracle.EconomyPhaseAt(T.AddTicks(step * i)).Label);
        Assert.Subset(new HashSet<string> { "Boom", "Bust", "Recovery", "Slowing" }, labels);
        Assert.True(labels.Count >= 3, "a full cycle passes through several phases");
    }
}
