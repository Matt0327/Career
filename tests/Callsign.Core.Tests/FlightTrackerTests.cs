using Callsign.Core.Flight;
using Callsign.SimConnect;
using Xunit;

namespace Callsign.Core.Tests;

public class FlightTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TelemetrySnapshot Snap(
        int sec, double alt, double gs, double vs, bool onGround,
        double lat = 52.0, double lon = 4.0, double fuel = 500, string title = "Cessna 172")
        => new()
        {
            Sequence = sec,
            CapturedAt = T0.AddSeconds(sec),
            AltitudeFt = alt,
            IndicatedAirspeedKts = gs,
            GroundSpeedKts = gs,
            VerticalSpeedFpm = vs,
            LatitudeDeg = lat,
            LongitudeDeg = lon,
            FuelQuantityLbs = fuel,
            OnGround = onGround,
            AircraftTitle = title,
        };

    private static FlightTracker FlyStandardLeg()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true, lat: 52.0, lon: 4.0, fuel: 500));    // parked
        t.Observe(Snap(30, 0, 15, 0, onGround: true, fuel: 499));                        // taxi
        t.Observe(Snap(60, 50, 75, 700, onGround: false, lat: 52.0, lon: 4.0, fuel: 495)); // takeoff
        t.Observe(Snap(120, 3000, 120, 500, onGround: false, fuel: 480));                // climb
        t.Observe(Snap(600, 9000, 150, 0, onGround: false, fuel: 400));                  // cruise
        t.Observe(Snap(1200, 1000, 95, -600, onGround: false, fuel: 360));              // approach
        t.Observe(Snap(1260, 50, 70, -150, onGround: false, lat: 53.12, lon: 6.11, fuel: 355)); // short final
        t.Observe(Snap(1265, 0, 60, 0, onGround: true, lat: 53.12, lon: 6.11, fuel: 354));      // touchdown
        t.Observe(Snap(1320, 0, 0, 0, onGround: true, lat: 53.12, lon: 6.11, fuel: 353));       // shutdown
        return t;
    }

    [Fact]
    public void TracksAFullLeg_AndProducesAScoredRecord()
    {
        var tracker = FlyStandardLeg();

        Assert.Equal(FlightPhase.Shutdown, tracker.Phase);
        var r = tracker.Result;
        Assert.NotNull(r);
        Assert.Equal("Cessna 172", r!.AircraftTitle);
        Assert.Equal(-150, r.TouchdownFpm);                       // last airborne descent rate
        Assert.Equal(9000, r.MaxAltitudeFt);
        Assert.Equal(T0.AddSeconds(60), r.DepartedAt);
        Assert.Equal(T0.AddSeconds(1265), r.ArrivedAt);
        Assert.Equal(TimeSpan.FromSeconds(1205), r.BlockTime);
        Assert.Equal(141, r.FuelUsedLbs);                         // 495 at takeoff - 354 at touchdown
        Assert.True(r.DistanceNm > 100);                          // 52,4 -> 53.12,6.11 is ~100+ nm
    }

    [Fact]
    public void Emits_Takeoff_And_Touchdown_Events()
    {
        var r = FlyStandardLeg().Result!;

        Assert.Contains(r.Events, e => e.Message == "Takeoff" && e.Severity == FlightEventSeverity.Info);
        var landing = Assert.Single(r.Events, e => e.Message.StartsWith("Landed"));
        Assert.Equal(FlightEventSeverity.Success, landing.Severity); // -150 fpm is a good landing
        Assert.Equal("Landed at -150 fpm", landing.Message);
    }

    [Theory]
    [InlineData(-120, FlightEventSeverity.Success)]
    [InlineData(-350, FlightEventSeverity.Info)]
    [InlineData(-800, FlightEventSeverity.Warning)]
    public void Landing_Severity_ScalesWithTouchdownRate(double touchdownFpm, FlightEventSeverity expected)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));                // takeoff
        t.Observe(Snap(60, 30, 60, touchdownFpm, onGround: false));       // short final at the test rate
        t.Observe(Snap(61, 0, 55, 0, onGround: true));                    // touchdown

        var landing = Assert.Single(t.Events, e => e.Message.StartsWith("Landed"));
        Assert.Equal(expected, landing.Severity);
    }

    [Fact]
    public void Warns_On_Taxi_Overspeed()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(5, 0, 40, 0, onGround: true)); // 40 kt taxi

        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Taxi speed"));
    }

    [Fact]
    public void GoAround_KeepsTheFinalTouchdown_NotTheBounce()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));   // takeoff
        t.Observe(Snap(60, 20, 60, -500, onGround: false));  // descending
        t.Observe(Snap(61, 0, 55, 0, onGround: true));       // first touch (-500)
        t.Observe(Snap(63, 40, 65, 600, onGround: false));   // bounce/go-around back up
        t.Observe(Snap(120, 20, 55, -100, onGround: false)); // second final
        t.Observe(Snap(121, 0, 50, 0, onGround: true));      // second touch (-100)
        t.Observe(Snap(160, 0, 0, 0, onGround: true));       // stop

        Assert.NotNull(t.Result);
        Assert.Equal(-100, t.Result!.TouchdownFpm); // the final landing, not the -500 bounce
    }
}
