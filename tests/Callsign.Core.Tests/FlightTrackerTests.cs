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
        double windKts = 0, double windDir = 0, double heading = 0, double visSm = 10,
        double tdNormalFps = 0, double tdLateralFps = 0,
        double totalWt = 0, double maxGross = 0, double cg = 0, double cgFwd = 0, double cgAft = 0,
        double engDmg = 0, double xtrack = 0, double ice = 0)
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
            TouchdownNormalVelocityFps = tdNormalFps,
            TouchdownLateralVelocityFps = tdLateralFps,
            TotalWeightLbs = totalWt,
            MaxGrossWeightLbs = maxGross,
            CgPercent = cg,
            CgFwdLimit = cgFwd,
            CgAftLimit = cgAft,
            EngineDamagePercent = engDmg,
            ApproachCrossTrackFt = xtrack,
            StructuralIcePct = ice,
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

    // ── The Fun Dial (Phase 9, law L9): coach the small stuff, never penalise it ──────────────────

    private static FlightTracker LegWith(double bank = 0, double g = 1.0)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));                                 // parked
        t.Observe(Snap(10, 50, 70, 500, onGround: false));                          // takeoff
        t.Observe(Snap(30, 3000, 120, 0, onGround: false, bank: bank, g: g));       // the moment under test
        t.Observe(Snap(60, 30, 60, -120, onGround: false));                         // clean short final
        t.Observe(Snap(61, 0, 55, 0, onGround: true));                              // touchdown
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                              // shutdown → Result ready
        return t;
    }

    [Fact]
    public void FunDial_SteepishBank_Coaches_WithNoPenalty()
    {
        var t = LegWith(bank: 42); // in the 35–60° coaching band, short of the exceedance
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("Bank"));
        Assert.DoesNotContain(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Steep bank"));
        Assert.Equal(0, t.Result!.ViolationPoints); // a nudge is never a straf (L9)
    }

    [Fact]
    public void FunDial_FirmG_Coaches_WithNoPenalty()
    {
        var t = LegWith(g: 2.0); // firm (1.8–2.5), short of the over-g exceedance
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("Load"));
        Assert.DoesNotContain(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("High load"));
        Assert.Equal(0, t.Result!.ViolationPoints);
    }

    [Fact]
    public void SteepBank_OverTheGate_IsStillAScoredViolation_NotJustCoaching()
    {
        var t = LegWith(bank: 68); // past the 60° exceedance — the real limit still bites
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Steep bank"));
        Assert.True(t.Result!.ViolationPoints > 0);
    }

    // ── Phase 9c: authoritative touchdown grade from the sim's captured contact state ─────────────

    [Fact]
    public void AuthoritativeTouchdown_CatchesAPeakTheSamplingMissed()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));
        t.Observe(Snap(59, 40, 65, -100, onGround: false));               // sampled: a soft-looking -100 fpm final
        t.Observe(Snap(60, 0, 55, 0, onGround: true, tdNormalFps: 10));    // sim captured 10 ft/s = -600 fpm at contact
        t.Observe(Snap(120, 0, 0, 0, onGround: true));                     // shutdown

        Assert.Equal(-600, t.Result!.TouchdownFpmWorst3);                  // the authoritative rate drives the grade
        Assert.Contains(t.Events, e => e.Message == "Landed at -600 fpm"); // and the log line agrees
    }

    [Fact]
    public void AuthoritativeTouchdown_NeverSoftensASlam()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));
        t.Observe(Snap(59, 40, 65, -850, onGround: false));               // sampled: a genuine slam
        t.Observe(Snap(60, 0, 55, 0, onGround: true, tdNormalFps: 2));     // sim reports a soft 2 ft/s = -120
        t.Observe(Snap(120, 0, 0, 0, onGround: true));

        Assert.Equal(-850, t.Result!.TouchdownFpmWorst3);                  // Min keeps the hardest — can't be gamed softer
    }

    [Fact]
    public void SideLoadTouchdown_Coaches_WithNoPenalty()
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));
        t.Observe(Snap(60, 30, 60, -120, onGround: false));               // clean final
        t.Observe(Snap(61, 0, 55, 0, onGround: true, tdLateralFps: 7));    // firm de-crab side-load
        t.Observe(Snap(120, 0, 0, 0, onGround: true));

        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("Side-load"));
        Assert.Equal(0, t.Result!.ViolationPoints); // technique note, not a penalty (L9)
    }

    // ── Phase 9d: weight & balance at takeoff, on the Fun Dial ────────────────────────────────────

    // A minimal leg whose TAKEOFF frame carries the weight & balance under test.
    private static FlightTracker WbLeg(double totalWt = 0, double maxGross = 0, double cg = 0, double cgFwd = 0, double cgAft = 0)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false, totalWt: totalWt, maxGross: maxGross, cg: cg, cgFwd: cgFwd, cgAft: cgAft)); // liftoff
        t.Observe(Snap(60, 30, 60, -120, onGround: false));
        t.Observe(Snap(61, 0, 55, 0, onGround: true));
        t.Observe(Snap(120, 0, 0, 0, onGround: true));
        return t;
    }

    [Fact]
    public void WeightBalance_GrossOverweight_WarnsAndScores()
    {
        var t = WbLeg(totalWt: 2700, maxGross: 2500); // 8% over MTOW — a real overload
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Overweight"));
        Assert.True(t.Result!.ViolationPoints > 0);
    }

    [Fact]
    public void WeightBalance_SlightlyOver_Coaches_WithNoPenalty()
    {
        var t = WbLeg(totalWt: 2560, maxGross: 2500); // ~2% over — a nudge only
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("MTOW"));
        Assert.DoesNotContain(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Overweight"));
        Assert.Equal(0, t.Result!.ViolationPoints); // a hair over is never a straf (L9)
    }

    [Fact]
    public void WeightBalance_WithinLimits_AndUnreported_AreSilent()
    {
        Assert.DoesNotContain(WbLeg(totalWt: 2300, maxGross: 2500).Events, e => e.Message.Contains("MTOW") || e.Message.Contains("Overweight"));
        Assert.DoesNotContain(WbLeg().Events, e => e.Message.Contains("MTOW") || e.Message.Contains("CG")); // no sim data → no check (L10)
    }

    [Fact]
    public void WeightBalance_GrossCgOutOfEnvelope_WarnsAndScores()
    {
        var t = WbLeg(cg: 45, cgFwd: 15, cgAft: 35); // 10% past the aft limit, envelope width 20 → gross (>5)
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("CG"));
        Assert.True(t.Result!.ViolationPoints > 0);
    }

    [Fact]
    public void WeightBalance_CgNearTheEdge_Coaches_WithNoPenalty()
    {
        var t = WbLeg(cg: 37, cgFwd: 15, cgAft: 35); // just past the aft limit, within the gross margin
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("CG"));
        Assert.Equal(0, t.Result!.ViolationPoints);
    }

    // ── Phase 9e: engine wear from the sim's OWN damage model, on the Fun Dial ─────────────────────────

    // A minimal leg that lands, with the engine reading `baseline` damage at first sight and `peak` in cruise.
    private static FlightTracker EngLeg(double baseline, double peak)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true, engDmg: baseline));        // parked — sets the baseline
        t.Observe(Snap(10, 50, 70, 500, onGround: false, engDmg: baseline));  // liftoff
        t.Observe(Snap(300, 9000, 150, 0, onGround: false, engDmg: peak));    // cruise — damage accrues
        t.Observe(Snap(600, 30, 60, -120, onGround: false, engDmg: peak));    // final
        t.Observe(Snap(601, 0, 55, 0, onGround: true, engDmg: peak));         // touchdown
        t.Observe(Snap(660, 0, 0, 0, onGround: true, engDmg: peak));          // shutdown → Complete()
        return t;
    }

    [Fact]
    public void EngineAbuse_SustainedDamage_CoachesAndAccrues_WithoutScoring()
    {
        var t = EngLeg(baseline: 0, peak: 8); // the sim recorded 8% of fresh engine damage this leg
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("Engine stress"));
        Assert.Equal(8.0, t.Result!.EngineDamagePctAccrued, 3);
        Assert.Equal(0, t.Result!.ViolationPoints); // it bills through engine wear, never the flight score (L9)
    }

    [Fact]
    public void EngineAbuse_PreExistingDamage_IsNotRebilled()
    {
        var t = EngLeg(baseline: 30, peak: 33); // already-worn engine; only THIS leg's 3% is the pilot's to answer for
        Assert.Equal(3.0, t.Result!.EngineDamagePctAccrued, 3);
    }

    [Fact]
    public void EngineAbuse_UnderTheDeadband_And_NoSimData_AreFreeAndSilent()
    {
        var blip = EngLeg(baseline: 0, peak: 0.3); // trivial jitter below the noise deadband
        Assert.Equal(0.0, blip.Result!.EngineDamagePctAccrued);
        Assert.DoesNotContain(blip.Events, e => e.Message.Contains("Engine stress"));

        var noData = EngLeg(baseline: 0, peak: 0); // an aircraft that never publishes damage (L10)
        Assert.Equal(0.0, noData.Result!.EngineDamagePctAccrued);
        Assert.DoesNotContain(noData.Events, e => e.Message.Contains("Engine stress"));
    }

    [Fact]
    public void EngineAbuse_LoneSpikeFrame_IsFilteredOut_NeverDestroysTheEngine()
    {
        // A single bogus 100% damage frame between sane readings must NOT wear the engine (two-frame confirm):
        // real cumulative damage persists, a glitch doesn't. Guards against "one bad sample = full overhaul".
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true, engDmg: 0));            // baseline 0
        t.Observe(Snap(10, 50, 70, 500, onGround: false, engDmg: 0));      // liftoff
        t.Observe(Snap(300, 9000, 150, 0, onGround: false, engDmg: 100));  // ONE garbage spike
        t.Observe(Snap(310, 9000, 150, 0, onGround: false, engDmg: 0));    // back to sane
        t.Observe(Snap(600, 30, 60, -120, onGround: false, engDmg: 0));    // final
        t.Observe(Snap(601, 0, 55, 0, onGround: true, engDmg: 0));         // touchdown
        t.Observe(Snap(660, 0, 0, 0, onGround: true, engDmg: 0));          // shutdown → Complete()
        Assert.Equal(0.0, t.Result!.EngineDamagePctAccrued);
        Assert.DoesNotContain(t.Events, e => e.Message.Contains("Engine stress"));
    }

    // ── Phase 10b: approach precision (centreline tracking) ────────────────────────────────────────

    // A leg with `count` below-gate approach samples at a fixed cross-track, ending in a touchdown.
    private static FlightTracker ApproachLeg(double xtrack, int count = 5)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));                 // parked
        t.Observe(Snap(10, 50, 70, 500, onGround: false));           // takeoff
        t.Observe(Snap(60, 3000, 120, 0, onGround: false));          // cruise (above the gate)
        for (int i = 0; i < count; i++)                              // below-gate final, descending, off by `xtrack`
            t.Observe(Snap(600 + i * 5, 800 - i * 100, 90, -500, onGround: false, agl: 800 - i * 100, xtrack: xtrack));
        t.Observe(Snap(700, 0, 50, 0, onGround: true));              // touchdown
        t.Observe(Snap(760, 0, 0, 0, onGround: true));               // shutdown → Complete()
        return t;
    }

    [Fact]
    public void Comfort_SmoothLegScoresHigh_RoughLegScoresLow()
    {
        var smooth = new FlightTracker();
        smooth.Observe(Snap(0, 0, 0, 0, onGround: true));
        smooth.Observe(Snap(10, 50, 70, 500, onGround: false));               // gentle climb-out
        smooth.Observe(Snap(300, 5000, 150, 0, onGround: false));             // cruise
        smooth.Observe(Snap(600, 500, 80, -180, onGround: false, agl: 500));  // easing down the final
        smooth.Observe(Snap(610, 200, 70, -150, onGround: false, agl: 200));
        smooth.Observe(Snap(615, 30, 65, -80, onGround: false, agl: 30));
        smooth.Observe(Snap(616, 0, 55, 0, onGround: true));                  // a greaser
        smooth.Observe(Snap(660, 0, 0, 0, onGround: true));
        Assert.True(smooth.Result!.ComfortScore >= 90, $"a gentle leg should feel smooth (got {smooth.Result!.ComfortScore})");

        var rough = new FlightTracker();
        rough.Observe(Snap(0, 0, 0, 0, onGround: true));
        rough.Observe(Snap(10, 50, 70, 500, onGround: false, bank: 55, g: 2.3)); // a violent airborne manoeuvre
        rough.Observe(Snap(600, 30, 60, -700, onGround: false, bank: 40));
        rough.Observe(Snap(601, 0, 55, 0, onGround: true, g: 2.0));              // and a hard arrival
        rough.Observe(Snap(660, 0, 0, 0, onGround: true));
        Assert.True(rough.Result!.ComfortScore < 60, $"a rough leg should feel rough (got {rough.Result!.ComfortScore})");
    }

    [Fact]
    public void ApproachPrecision_OnCentreline_ScoresFull_OffCentreline_Docks()
    {
        var on = ApproachLeg(0);       // on the centreline (and the default 0 → isolation: unchanged)
        var off = ApproachLeg(800);    // well past the 400 ft limit
        Assert.Equal(100, on.Result!.ApproachScore);
        Assert.True(off.Result!.ApproachScore < on.Result!.ApproachScore);
    }

    [Fact]
    public void ApproachPrecision_DriftIntoTheCoachBand_CoachesOnce_WithoutDockingTheScore()
    {
        var t = ApproachLeg(300); // 250 < 300 < 400 → a nudge, but inside the score limit
        Assert.Single(t.Events, e => e.Message.Contains("centreline"));
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("centreline"));
        Assert.Equal(100, t.Result!.ApproachScore); // still on profile for the score (the Fun Dial: coach before it costs)
    }

    // ── Phase 10d: structural icing ───────────────────────────────────────────────────────────────

    // A completed leg with `ice`% on `heavyFrames` consecutive airborne samples, then clear air to landing.
    private static FlightTracker IceLeg(double ice, int heavyFrames)
    {
        var t = new FlightTracker();
        t.Observe(Snap(0, 0, 0, 0, onGround: true));
        t.Observe(Snap(10, 50, 70, 500, onGround: false));                                 // liftoff
        for (int i = 0; i < heavyFrames; i++)
            t.Observe(Snap(100 + i * 10, 8000, 150, 0, onGround: false, ice: ice));         // cruise in the ice
        t.Observe(Snap(600, 30, 60, -120, onGround: false));                               // clear air, final
        t.Observe(Snap(601, 0, 55, 0, onGround: true));                                     // touchdown
        t.Observe(Snap(660, 0, 0, 0, onGround: true));                                      // shutdown → Complete()
        return t;
    }

    [Fact]
    public void Icing_SustainedHeavy_CoachesThenWarns_AndDocksTheEnrouteScore()
    {
        var t = IceLeg(60, heavyFrames: 3);
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Coaching && e.Message.Contains("Ice building"));
        Assert.Contains(t.Events, e => e.Severity == FlightEventSeverity.Warning && e.Message.Contains("Heavy icing"));
        Assert.True(t.Result!.ViolationPoints > 0);
        Assert.True(t.Result!.EnrouteScore < 100);
    }

    [Fact]
    public void Icing_ABriefHeavyBlip_Coaches_ButDoesNotYetWarn()
    {
        var t = IceLeg(60, heavyFrames: 1); // heavy on ONE sample only — not sustained
        Assert.Contains(t.Events, e => e.Message.Contains("Ice building"));
        Assert.DoesNotContain(t.Events, e => e.Message.Contains("Heavy icing"));
        Assert.Equal(0, t.Result!.ViolationPoints);
    }

    [Fact]
    public void Icing_LightIce_Coaches_WithNoPenalty()
    {
        var t = IceLeg(15, heavyFrames: 3); // present, but below the heavy threshold
        Assert.Contains(t.Events, e => e.Message.Contains("Ice building"));
        Assert.DoesNotContain(t.Events, e => e.Message.Contains("Heavy icing"));
        Assert.Equal(0, t.Result!.ViolationPoints);
    }

    [Fact]
    public void Icing_ClearAir_IsSilent()
    {
        var t = IceLeg(0, heavyFrames: 3);
        Assert.DoesNotContain(t.Events, e => e.Message.Contains("ce building") || e.Message.Contains("cing"));
        Assert.Equal(0, t.Result!.ViolationPoints);
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
