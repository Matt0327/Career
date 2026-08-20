using System.Reflection;
using Callsign.Core.Economy;
using Callsign.Core.Flight;
using Callsign.Core.World;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 9b — the resolver + the firewall. Live weather sits ABOVE the pure WorldOracle; OFF must be
/// byte-identical to Phase 8, and the autonomous economy must be structurally unable to reach the live seam.</summary>
public class WeatherProviderTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeWeatherSource : IWeatherSource
    {
        public WeatherReading? Reading;
        public bool Throw;
        public int Prefetches;
        public WeatherReading? TryObserve(double lat, double lon, DateTimeOffset now) => Throw ? throw new InvalidOperationException("boom") : Reading;
        public void Prefetch(double lat, double lon, string? icao) { if (Throw) throw new InvalidOperationException("boom"); Prefetches++; }
    }

    [Fact]
    public void Off_IsByteIdenticalToTheSyntheticModel()
    {
        var oracle = new WorldOracle(Cfg);
        var p = new WeatherProvider(oracle, NullWeatherSource.Instance, Cfg);
        foreach (var (lat, lon) in new[] { (51.5, -0.1), (40.6, -73.8), (-33.9, 151.2), (0.0, 0.0) })
            for (int h = 0; h < 48; h += 5)
            {
                var t = T0.AddHours(h);
                Assert.Equal(oracle.WeatherAt(lat, lon, t), p.Observed(lat, lon, t).Weather); // display == synthetic
                Assert.False(p.Observed(lat, lon, t).IsLive);
                Assert.Equal(oracle.WeatherAt(lat, lon, t), p.MarketWeather(lat, lon, t));      // market == synthetic
            }
    }

    [Fact]
    public void Observed_UsesLiveWhenPresent_ElseSynthetic()
    {
        var oracle = new WorldOracle(Cfg);
        var live = new WeatherReading(new Weather(180, 40, 55, 0.5, 200, 2, "Storm", "nasty"), IsLive: true, AsOf: T0.AddMinutes(-10));
        var src = new FakeWeatherSource { Reading = live };
        var p = new WeatherProvider(oracle, src, Cfg);

        var got = p.Observed(51.5, -0.1, T0);
        Assert.True(got.IsLive);
        Assert.Equal(live.Weather, got.Weather);
        Assert.Equal(live.AsOf, got.AsOf);

        src.Reading = null;
        var fell = p.Observed(51.5, -0.1, T0);
        Assert.False(fell.IsLive);
        Assert.Equal(oracle.WeatherAt(51.5, -0.1, T0), fell.Weather);
    }

    [Fact]
    public void MarketWeather_RespectsTheFeedsMarketSubToggle()
    {
        var oracle = new WorldOracle(Cfg);
        var live = new WeatherReading(new Weather(180, 40, 55, 0.5, 200, 2, "Storm", "nasty"), true, T0);
        var src = new FakeWeatherSource { Reading = live };

        var off = new WeatherProvider(oracle, src, Cfg); // LiveWeatherFeedsMarket default false
        Assert.Equal(oracle.WeatherAt(51.5, -0.1, T0), off.MarketWeather(51.5, -0.1, T0)); // synthetic even with a live reading

        var on = new WeatherProvider(oracle, src, new EconomyConfig { LiveWeatherFeedsMarket = true });
        Assert.Equal(live.Weather, on.MarketWeather(51.5, -0.1, T0)); // now the live reading feeds the market
    }

    [Fact]
    public void MarketWeather_PinsOneValuePerWindow_ThenReMaterializesNextWindow()
    {
        var oracle = new WorldOracle(Cfg);
        var a = new WeatherReading(new Weather(180, 40, 55, 0.5, 200, 2, "Storm", "A"), true, T0);
        var b = new WeatherReading(new Weather(0, 3, 3, 10, 25000, 20, "Clear", "B"), true, T0);
        var src = new FakeWeatherSource { Reading = a };
        var p = new WeatherProvider(oracle, src, new EconomyConfig { LiveWeatherFeedsMarket = true });

        var first = p.MarketWeather(51.5, -0.1, T0);
        Assert.Equal(a.Weather, first);
        src.Reading = b;                                        // the feed flips mid-window
        Assert.Equal(a.Weather, p.MarketWeather(51.5, -0.1, T0)); // pin holds → exact display==settlement

        var nextWindow = T0.Add(Cfg.WeatherWindow).Add(Cfg.WeatherWindow); // a fresh (cell, epoch)
        Assert.Equal(b.Weather, p.MarketWeather(51.5, -0.1, nextWindow));   // re-materialises with the new value
    }

    [Fact]
    public void AMisbehavingSource_DegradesToSynthetic_NeverThrows()
    {
        var oracle = new WorldOracle(Cfg);
        var src = new FakeWeatherSource { Throw = true };
        var p = new WeatherProvider(oracle, src, new EconomyConfig { LiveWeatherFeedsMarket = true });
        Assert.Equal(oracle.WeatherAt(51.5, -0.1, T0), p.Observed(51.5, -0.1, T0).Weather);
        Assert.Equal(oracle.WeatherAt(51.5, -0.1, T0), p.MarketWeather(51.5, -0.1, T0));
    }

    // ── Firewall proof (architectural) ────────────────────────────────────────────────────────────
    // The load-bearing invariant: the autonomous economy (reconcile), the pure oracle, and the scored flight can
    // never reach the live seam — no constructor of theirs takes an IWeatherSource/WeatherProvider. TradeService
    // is the deliberately-sanctioned interactive coupling (9b-2) and is exempt.

    [Theory]
    [InlineData(typeof(WorldOracle))]
    [InlineData(typeof(OperationsService))]
    [InlineData(typeof(FlightTracker))]
    [InlineData(typeof(JobBoardService))]
    public void FirewalledServices_TakeNoLiveWeatherDependency(Type service)
    {
        foreach (var ctor in service.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            foreach (var p in ctor.GetParameters())
                Assert.False(p.ParameterType == typeof(IWeatherSource) || p.ParameterType == typeof(WeatherProvider),
                    $"{service.Name} must not depend on the live weather seam — it would breach the firewall (found {p.ParameterType.Name}).");
    }

    [Fact]
    public void CoreAssembly_HasNoDirectSystemNetHttpReference()
        => Assert.DoesNotContain(typeof(WorldOracle).Assembly.GetReferencedAssemblies(),
            a => a.Name == "System.Net.Http"); // the live HTTP adapter is Host-only (project-graph firewall lock)
}
