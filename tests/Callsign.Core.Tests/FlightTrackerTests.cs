using Callsign.Core.Flight;
using Callsign.SimConnect;
using Xunit;

namespace Callsign.Core.Tests;

public class FlightTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TelemetrySnapshot Snap(
        int sec, double alt, double gs, double vs, bool onGround,
        double lat = 52.0, double lon = 4.0, double fuel = 500, string title = "Cessna 172",
        double bank = 0, double g = 1.0, bool stall = false, bool overspeed = false, double? agl = null,
        double simRate = 1.0, bool slew = false,
        double windKts = 0, double windDir = 0, double heading = 0, double visSm = 10)
        => new()
        {
            Sequence = sec,
            AmbientWindKts = windKts,
            AmbientWindDirDeg = windDir,
            HeadingDegTrue = heading,
            AmbientVisibilitySm = visSm,
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
            AltitudeAglFt = agl ?? alt, // no terrain in the scripts — AGL tracks indicated altitude
            BankDeg = bank,
            GForce = g,
            StallWarning = stall,
            OverspeedWarning = overspeed,
            SimRate = simRate,
            SlewActive = slew,
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
    public void LandingGrade_UsesWorstOfLastThree_NotACherryPickedSoftFrame()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 200, 70, 500, onGround: false));   // climb out
        // A dense flare: a hard sink, then a soft frame right before contact (the classic greaser cheat).
        t.Observe(Snap(60, 120, 70, -200, onGround: false));
        t.Observe(Snap(61, 40, 65, -720, onGround: false));   // hard sink
        t.Observe(Snap(62, 8, 60, -60, onGround: false));     // soft frame just before contact
        t.Observe(Snap(63, 0, 55, 0, onGround: true));        // touchdown
        t.Observe(Snap(120, 0, 0, 0, onGround: true));        // stop → complete

        var r = t.Result!;
        Assert.Equal(-60, r.TouchdownFpm);          // raw last-frame rate, unchanged behaviour
        Assert.Equal(-720, r.TouchdownFpmWorst3);   // worst of the last three (-200,-720,-60) — un-gameable
        Assert.True(r.LandingScore <= 10);          // graded on the -720, not the soft -60
    }

    [Fact]
    public void UnstableApproach_IsFlagged_AndLogged()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 300, 90, 800, onGround: false));    // climb out (not counted as approach)
        // Below the 1000 ft gate, diving well past -1000 fpm = an unstable approach.
        t.Observe(Snap(40, 800, 120, -1600, onGround: false));
        t.Observe(Snap(45, 400, 110, -1500, onGround: false));
        t.Observe(Snap(50, 60, 80, -900, onGround: false));
        t.Observe(Snap(51, 0, 60, 0, onGround: true));         // touchdown
        t.Observe(Snap(110, 0, 0, 0, onGround: true));         // stop

        var r = t.Result!;
        Assert.False(r.StabilizedApproach);
        Assert.True(r.ApproachScore < StableApproachThreshold);
        Assert.Contains(r.Events, e => e.Message == "Unstable approach" && e.Severity == FlightEventSeverity.Warning);
    }

    [Fact]
    public void Airborne_StallWarning_EmitsAScoredEvent_AndDocksEnroute()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 500, 70, 500, onGround: false));                  // takeoff
        t.Observe(Snap(20, 700, 55, 100, onGround: false, stall: true));     // stall warning airborne
        t.Observe(Snap(60, 30, 60, -120, onGround: false));                  // stable final
        t.Observe(Snap(61, 0, 55, 0, onGround: true));                       // touchdown
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                       // stop

        var r = t.Result!;
        Assert.Contains(r.Events, e => e.Message == "Stall warning" && e.Severity == FlightEventSeverity.Warning);
        Assert.True(r.ViolationPoints >= 20);
        Assert.True(r.EnrouteScore <= 80);
    }

    private const int StableApproachThreshold = 70;

    [Fact]
    public void ScoredLeg_IsMarkedScored_AndValidByDefault()
    {
        var r = FlyStandardLeg().Result!;
        Assert.True(r.Scored);       // the tracker graded it
        Assert.True(r.ScoreValid);   // nothing dodgy happened
    }

    [Fact]
    public void Slew_Airborne_VoidsTheScore_AndLogsIt()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 3000, 120, 500, onGround: false));                 // enroute
        t.Observe(Snap(20, 3000, 120, 0, onGround: false, slew: true));       // teleport/slew — a cheat
        t.Observe(Snap(60, 30, 60, -120, onGround: false));                   // final
        t.Observe(Snap(61, 0, 55, 0, onGround: true));                        // touchdown
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                        // stop

        var r = t.Result!;
        Assert.False(r.ScoreValid);
        Assert.Contains(r.Events, e => e.Message.Contains("Slew") && e.Severity == FlightEventSeverity.Warning);
    }

    [Fact]
    public void TimeAcceleration_NearTheGround_VoidsTheScore()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 3000, 120, 500, onGround: false));                 // enroute
        t.Observe(Snap(50, 400, 90, -600, onGround: false, simRate: 4));      // 4× compression below the gate
        t.Observe(Snap(60, 30, 60, -120, onGround: false));                   // final
        t.Observe(Snap(61, 0, 55, 0, onGround: true));                        // touchdown
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                        // stop

        Assert.False(t.Result!.ScoreValid);
    }

    [Fact]
    public void EnrouteTimeCompression_IsAllowed_NotACheat()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 5000, 140, 500, onGround: false));                 // climb
        t.Observe(Snap(300, 9000, 150, 0, onGround: false, simRate: 8));      // 8× on a high-altitude cruise — legit
        t.Observe(Snap(600, 30, 60, -120, onGround: false));                  // final, real time
        t.Observe(Snap(601, 0, 55, 0, onGround: true));                       // touchdown
        t.Observe(Snap(660, 0, 0, 0, onGround: true));                        // stop

        Assert.True(t.Result!.ScoreValid); // time-compressing the boring cruise is fine
    }

    [Fact]
    public void GoAround_GradesTheFinalApproachFresh_NotThePooledOne()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 300, 90, 800, onGround: false));   // climb out
        // an unstable first approach below the gate...
        t.Observe(Snap(40, 600, 120, -1600, onGround: false));
        t.Observe(Snap(45, 300, 110, -1500, onGround: false));
        // ...then a go-around: climb back above the gate abandons it
        t.Observe(Snap(60, 1500, 120, 900, onGround: false));
        // a textbook second approach, all within limits
        t.Observe(Snap(90, 800, 90, -600, onGround: false));
        t.Observe(Snap(95, 300, 80, -500, onGround: false));
        t.Observe(Snap(100, 40, 70, -120, onGround: false));
        t.Observe(Snap(101, 0, 60, 0, onGround: true));       // touchdown
        t.Observe(Snap(160, 0, 0, 0, onGround: true));        // stop

        var r = t.Result!;
        Assert.Equal(100, r.ApproachScore);   // graded on the clean final approach, not pooled with the aborted one
        Assert.True(r.StabilizedApproach);
        Assert.DoesNotContain(r.Events, e => e.Message == "Unstable approach"); // no spurious warning
    }

    [Fact]
    public void HardBounce_IsGradedOnTheSlam_NotTheGentleReSettle()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 200, 80, 500, onGround: false));   // takeoff
        t.Observe(Snap(60, 40, 70, -900, onGround: false));   // hard sink to the runway
        t.Observe(Snap(61, 0, 60, 0, onGround: true));        // slam (-900)
        t.Observe(Snap(62, 15, 62, 300, onGround: false));    // bounces back up but stays below the gate
        t.Observe(Snap(70, 5, 55, -60, onGround: false));     // settles
        t.Observe(Snap(71, 0, 50, 0, onGround: true));        // gentle re-touch (-60)
        t.Observe(Snap(120, 0, 0, 0, onGround: true));        // stop

        var r = t.Result!;
        Assert.Equal(-60, r.TouchdownFpm);           // raw last contact — unchanged behaviour
        Assert.True(r.TouchdownFpmWorst3 <= -800);   // the slam survives; the soft re-touch can't game it away
        Assert.True(r.LandingScore <= 10);           // graded on the -900 slam
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

    // ── Weather difficulty on the landing grade (Phase 8) ──────────────────────

    [Fact]
    public void WeatherLandingBonus_CalmClearIsZero_CrosswindAndLowVisRaiseIt_Capped()
    {
        Assert.Equal(0, FlightTracker.WeatherLandingBonus(0, 10));      // calm, clear → no bonus (the default path)
        Assert.True(FlightTracker.WeatherLandingBonus(25, 10) > 0);     // a full crosswind
        Assert.True(FlightTracker.WeatherLandingBonus(0, 0.5) > 0);     // low visibility
        Assert.True(FlightTracker.WeatherLandingBonus(25, 10) > FlightTracker.WeatherLandingBonus(10, 10)); // more wind, more bonus
        Assert.True(FlightTracker.WeatherLandingBonus(200, 0) <= 15);   // capped
    }

    // A firm landing (rawLanding below 100) in a stiff crosswind grades higher than the identical landing in calm.
    private static FlightRecord FlyLandingWith(double windKts, double windDir, double heading, double visSm = 10)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false, heading: heading));   // takeoff
        t.Observe(Snap(60, 200, 70, -150, onGround: false, heading: heading)); // short final at -150 fpm
        t.Observe(Snap(61, 0, 60, 0, onGround: true, heading: heading, windKts: windKts, windDir: windDir, visSm: visSm)); // touchdown, in these conditions
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                          // shutdown
        return t.Result!;
    }

    [Fact]
    public void Landing_InACrosswind_GradesHigherThanTheSameLandingInCalm()
    {
        int calm = FlyLandingWith(0, 0, 0).LandingScore;
        var gustyLeg = FlyLandingWith(20, 90, 0); // 20 kt wind at 90° to a runway heading of 0° = 20 kt crosswind
        Assert.True(gustyLeg.LandingScore > calm);                            // tough air earns grade back
        Assert.Contains(gustyLeg.Events, e => e.Message.Contains("Tough conditions")); // and it's announced (transparency)
    }
}
