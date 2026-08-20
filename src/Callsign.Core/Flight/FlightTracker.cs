using System.Linq;
using Callsign.Core.Geo;
using Callsign.SimConnect;

namespace Callsign.Core.Flight;

/// <summary>
/// A state machine that consumes the telemetry stream and records a flight: the phase transitions,
/// the touchdown vertical speed (the headline score), block time, distance, fuel burn, and a log of
/// scored events. Phase 7b adds a real, un-gameable assessment on top of the raw envelope: a
/// worst-of-three landing grade (fpm ∧ g ∧ bank), a stabilised-approach score, and an enroute
/// exceedance tally that emits scored events as they happen. Feed it snapshots one at a time via
/// <see cref="Observe"/> (in the app, wire it to <c>ISimTelemetrySource.TelemetryReceived</c>); it is
/// pure and deterministic, so scripted telemetry tests it without a sim. It never throws on odd input
/// — worst case it simply doesn't complete.
/// </summary>
public sealed class FlightTracker
{
    private const double TaxiSpeedKts = 3;       // above this, on the ground = taxiing
    private const double TaxiOverspeedKts = 30;  // brief §2.3 rough-handling: fast taxi is penalised
    private const double ClimbVsFpm = 200;
    private const double DescentVsFpm = -200;

    // Phase 7b — stabilised-approach gate + exceedance thresholds (all lenient at ship, per the plan).
    private const double GateAglFt = 1000;       // below this height the approach is judged
    private const double MaxDescentFpm = -1000;  // steeper than this below the gate = unstable
    private const double MaxBankDeg = 10;         // more bank than this below the gate = unstable
    private const int StableApproachMinScore = 70;
    // Approach precision (Phase 10b): centreline tracking below the gate. On the Fun Dial — a gentle nudge as you
    // drift, and the approach score only takes it as "off profile" past the harder limit. Lenient at ship (L3).
    private const double CoachApproachCrossTrackFt = 250; // drifting this far off the centreline → a coaching nudge
    private const double MaxApproachCrossTrackFt = 400;   // past this = the approach sample is off-profile (docks the score)
    private const double OverBankDeg = 60;        // an enroute over-bank exceedance
    private const double OverGHigh = 2.5, OverGLow = -1.0;
    private const int PtsOverspeed = 15, PtsStall = 20, PtsOverBank = 10, PtsOverG = 15;
    // The Fun Dial (Phase 9, law L9): a coaching BAND below each violation threshold. A minor deviation here
    // earns a friendly, UNSCORED nudge in the log — never a penalty. It only becomes a scored exceedance if it
    // crosses the harder limit above.
    private const double CoachBankDeg = 35;              // steep-ish, but not an exceedance
    private const double CoachGHigh = 1.8, CoachGLow = -0.3; // firm, but not an exceedance
    private const double CoachLateralFps = 4;            // a firm de-crab side-load at touchdown → a nudge (Phase 9c)
    // Weight & balance (Phase 9d, on the Fun Dial): a hair over coaches; a gross excess is a warned consequence.
    private const double OverweightGrossFactor = 1.05;   // >5% over MTOW at takeoff = a real overload
    private const double CgGrossEnvelopeFrac = 0.25;     // beyond a CG limit by >25% of the envelope width = gross
    private const int PtsOverweight = 15, PtsCg = 12;    // scored only for the GROSS case
    // Engine wear (Phase 9e, on the Fun Dial): a deadband below which the sim's damage reading is treated as
    // noise — no cost, no nudge. At/above it the first sign earns a coaching nudge (L4) and the accrued damage
    // is priced into engine wear at settlement. This is the "sustained abuse bills, brief exceedance is free" line.
    private const double EngStressDeadbandPct = 0.5;

    // Phase 8 — tough conditions earn a landing back some grade: a firm touchdown in a 25 kt crosswind or
    // low visibility is a better piece of flying than the same numbers in calm, clear air.
    private const double XwindFullKts = 25;              // crosswind at which the difficulty maxes out
    private const double VisEasySm = 5, VisHardSm = 1;   // below 5 sm vis starts to bite; 1 sm is fully hard
    private const int MaxWeatherLandingBonus = 15;       // the most tough conditions can lift a landing grade

    private readonly List<FlightEvent> _events = [];

    private bool _wasAirborne;
    private bool _taxiWarned;

    private bool _inFlight;
    private bool _landed;
    private string _title = "";
    private DateTimeOffset _departedAt;
    private DateTimeOffset _arrivedAt;
    private double _depLat, _depLon, _depFuel;
    private double _arrLat, _arrLon, _arrFuel;
    private double _maxAltFt;
    private double _lastAirborneVs;
    private double _touchdownFpm;

    // Phase 7b working state.
    private readonly List<double> _recentVs = []; // last ≤3 airborne descent samples (reset on a real go-around)
    private double _lastAirborneBankDeg;
    private double _lastAirborneG = 1.0;
    // The WORST across every ground contact of the landing (a bounce has more than one), so a hard slam
    // followed by a gentle re-settle still grades on the slam — a bounce can't game the landing away.
    private double _worstFpm;         // most-negative sink at contact; 0 = no contact yet
    private double _worstG = 1.0;     // hardest vertical g at contact
    private double _worstBank;        // steepest bank at contact
    // Phase 10c — the whole-flight ride envelope, for the passenger-comfort grade.
    private double _peakBankDeg;      // steepest bank anywhere airborne
    private double _peakGDev;         // largest |g − 1| anywhere airborne
    // The sim's actual conditions at touchdown (Phase 8) — read, never commanded (law L7).
    private double _tdWindKts, _tdWindDir, _tdHeading, _tdVisSm = 10;
    private int _approachPass, _approachTotal;
    private int _violationPoints;
    private bool _overspeedWarned, _stallWarned, _bankWarned, _gWarned;
    private bool _bankCoached, _gCoached; // Phase 9 (L9): fire the friendly nudge at most once each
    private bool _localizerCoached;       // Phase 10b: nudge once when drifting off the approach centreline
    private bool _scoreValid = true; // flight-integrity monitoring (Phase 7c anti-cheat)
    // Phase 9e — engine damage from the sim's own model: baseline at first sight, monotonic max; the leg's
    // accrued damage (max − baseline) is priced into engine wear at settlement. _engDmgPrev holds the previous
    // raw reading so a rise must be confirmed across two samples before it bills (single-frame spike guard).
    private double _engDmgBaseline = -1, _engDmgMax, _engDmgPrev;
    private bool _engStressCoached;

    public FlightPhase Phase { get; private set; } = FlightPhase.Parked;
    public IReadOnlyList<FlightEvent> Events => _events;

    /// <summary>Set once the flight is complete (landed, then stopped). Null until then.</summary>
    public FlightRecord? Result { get; private set; }

    public void Observe(TelemetrySnapshot t)
    {
        _title = t.AircraftTitle;
        bool airborne = !t.OnGround;

        MeterEngineStress(t); // Phase 9e — accrue the sim's own engine damage across the WHOLE leg (ground run-up too)

        if (airborne)
            ObserveAirborne(t);
        else
            ObserveOnGround(t);

        _wasAirborne = airborne;
    }

    // Meter engine abuse from the sim's OWN cumulative damage figure (Phase 9e), on the Fun Dial (L9). Baseline
    // at first sight so a tail loaded with pre-existing damage isn't re-billed; track the monotonic max so a
    // mid-leg reading can only ever add authoritative wear, never soften it (un-gameable). Once the accrued
    // damage clears the noise deadband, the first sign earns ONE coaching nudge — the "you're doing real harm,
    // back off" warning (L4) — while the wear itself bills later, at settlement, through the maintenance path.
    private void MeterEngineStress(TelemetrySnapshot t)
    {
        double dmg = t.EngineDamagePercent;
        if (dmg < 0)
            return; // a garbage reading — ignore it rather than baselining on it
        if (_engDmgBaseline < 0)
        {
            _engDmgBaseline = _engDmgMax = _engDmgPrev = dmg; // first sight
        }
        else
        {
            // Confirm a rise before it bills: a lone spike frame is min'd away by its lower neighbour, so a
            // single bogus 100% sample can't wear the engine to an overhaul — only damage that PERSISTS across
            // two samples counts (L9: sustained, not a blip). Real cumulative damage is monotonic, so this
            // never discards genuine wear; a sim "repair" that lowers the reading is ignored (max stays sticky).
            double confirmed = Math.Min(dmg, _engDmgPrev);
            if (confirmed > _engDmgMax)
                _engDmgMax = confirmed;
            _engDmgPrev = dmg;
        }
        if (_engDmgMax - _engDmgBaseline >= EngStressDeadbandPct && !_engStressCoached)
        {
            _engStressCoached = true;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching,
                "Engine stress registering — ease the power and watch your temps"));
        }
    }

    // The leg's accrued engine damage, past the noise deadband (0 below it, or when the sim never reported).
    private double EngineDamageAccrued()
        => _engDmgBaseline >= 0 && _engDmgMax - _engDmgBaseline >= EngStressDeadbandPct
            ? _engDmgMax - _engDmgBaseline : 0;

    private void ObserveAirborne(TelemetrySnapshot t)
    {
        _maxAltFt = Math.Max(_maxAltFt, t.AltitudeFt);
        _lastAirborneVs = t.VerticalSpeedFpm;
        _lastAirborneBankDeg = Math.Abs(t.BankDeg);
        _lastAirborneG = Math.Abs(t.GForce);
        _peakBankDeg = Math.Max(_peakBankDeg, _lastAirborneBankDeg);      // Phase 10c — ride envelope
        _peakGDev = Math.Max(_peakGDev, Math.Abs(t.GForce - 1.0));

        if (!_wasAirborne)
        {
            if (!_inFlight)
            {
                _inFlight = true;
                _departedAt = t.CapturedAt;
                _depLat = t.LatitudeDeg;
                _depLon = t.LongitudeDeg;
                _depFuel = t.FuelQuantityLbs;
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Info, "Takeoff"));
                CheckWeightAndBalance(t); // Phase 9d — judge the load at the moment of liftoff
            }
            else
            {
                // airborne again after a touchdown: a go-around or a bounce (which one is decided below).
                _landed = false;
            }
            Phase = FlightPhase.Takeoff;
        }
        else
        {
            Phase = t.VerticalSpeedFpm > ClimbVsFpm ? FlightPhase.Climb
                  : t.VerticalSpeedFpm < DescentVsFpm ? FlightPhase.Approach
                  : FlightPhase.Cruise;
        }

        // A real go-around abandons the approach: once we climb back above the gate having already flown
        // one, grade the NEXT approach and the next touchdown fresh. A low bounce never climbs above the
        // gate, so its hard impact stays in the worst-across-contacts trackers and can't be gamed away.
        double aglNow = t.AltitudeAglFt > 0 ? t.AltitudeAglFt : t.AltitudeFt;
        if (_approachTotal > 0 && aglNow >= GateAglFt && t.VerticalSpeedFpm > ClimbVsFpm)
        {
            _approachPass = 0; _approachTotal = 0;
            _recentVs.Clear();
            _worstFpm = 0; _worstG = 1.0; _worstBank = 0;
        }

        PushRecentVs(t.VerticalSpeedFpm);
        AssessApproach(t);
        CheckViolations(t);
        CheckIntegrity(t);
    }

    private void ObserveOnGround(TelemetrySnapshot t)
    {
        if (_wasAirborne && _inFlight)
        {
            // touchdown: this contact (there may be more if it bounces). The raw rate is the last airborne
            // sample; the grade tracks the WORST across contacts — worst of the last three of THIS contact,
            // min'd with earlier contacts — so a soft frame OR a bounce can't game the landing away.
            double contactWorstFpm = _recentVs.Count > 0 ? _recentVs.Min() : _lastAirborneVs;
            // Phase 9c: fold in the sim's captured touchdown rate. As a descent it's negative; Min keeps the
            // HARDEST, so the authoritative value can only sharpen the grade (catch a peak the sampling missed),
            // never soften a slam. Default 0 = not reported → the sampled worst-of-three stands (L10).
            double authFpm = _lastAirborneVs;
            if (t.TouchdownNormalVelocityFps != 0)
            {
                double simTdFpm = -Math.Abs(t.TouchdownNormalVelocityFps) * 60.0;
                contactWorstFpm = Math.Min(contactWorstFpm, simTdFpm);
                authFpm = Math.Min(_lastAirborneVs, simTdFpm); // display the HARDER of the two, so a bounce can't understate
            }
            _worstFpm = Math.Min(_worstFpm, contactWorstFpm);
            _worstG = Math.Max(_worstG, Math.Max(Math.Abs(t.GForce), _lastAirborneG));
            _worstBank = Math.Max(_worstBank, _lastAirborneBankDeg);
            _touchdownFpm = authFpm; // authoritative rate when the sim reports it, else the raw last-frame rate
            _tdWindKts = t.AmbientWindKts; _tdWindDir = t.AmbientWindDirDeg; _tdHeading = t.HeadingDegTrue; _tdVisSm = t.AmbientVisibilitySm;
            _arrivedAt = t.CapturedAt;
            _arrLat = t.LatitudeDeg;
            _arrLon = t.LongitudeDeg;
            _arrFuel = t.FuelQuantityLbs;
            _landed = true;
            Phase = FlightPhase.Landing;

            // An unstable approach is called out just before the touchdown line, so the log reads in order.
            if (_approachTotal > 0 && ApproachScore() < StableApproachMinScore)
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, "Unstable approach"));

            _events.Add(new FlightEvent(t.CapturedAt, LandingSeverity(_touchdownFpm),
                $"Landed at {_touchdownFpm:F0} fpm"));
            // Fun Dial (L9): a firm de-crab side-load at touchdown coaches, it doesn't penalise (Phase 9c).
            if (Math.Abs(t.TouchdownLateralVelocityFps) > CoachLateralFps)
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching,
                    $"Side-load on touchdown — {Math.Abs(t.TouchdownLateralVelocityFps):F0} ft/s, de-crab a touch earlier"));
            return;
        }

        if (t.GroundSpeedKts >= TaxiSpeedKts)
        {
            Phase = _inFlight && _landed ? FlightPhase.Landing : FlightPhase.Taxi;
            if (t.GroundSpeedKts > TaxiOverspeedKts && !_taxiWarned)
            {
                _taxiWarned = true;
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning,
                    $"Taxi speed {t.GroundSpeedKts:F0} kt exceeds {TaxiOverspeedKts:F0} kt"));
            }
        }
        else if (_inFlight && _landed)
        {
            Phase = FlightPhase.Shutdown;
            Complete();
        }
        else
        {
            Phase = FlightPhase.Parked;
        }
    }

    private void PushRecentVs(double vs)
    {
        _recentVs.Add(vs);
        if (_recentVs.Count > 3)
            _recentVs.RemoveAt(0);
    }

    // A stabilised approach holds a sane descent rate and wings-near-level below the gate, with no stall
    // or overspeed annunciation. We score the fraction of below-gate samples that stayed within limits.
    private void AssessApproach(TelemetrySnapshot t)
    {
        if (t.VerticalSpeedFpm > ClimbVsFpm)
            return; // climbing out through the gate is not an approach
        double agl = t.AltitudeAglFt > 0 ? t.AltitudeAglFt : t.AltitudeFt;
        if (agl >= GateAglFt)
            return;
        _approachTotal++;
        // Precision (Phase 10b): a sample off the centreline past the hard limit is "off profile", folding
        // centreline tracking into the existing approach score. Default 0 (no active nav / visual) is on the
        // centreline, so a visual approach is never docked for it (L10).
        double xtrack = Math.Abs(t.ApproachCrossTrackFt);
        bool onCentreline = xtrack <= MaxApproachCrossTrackFt;
        bool ok = t.VerticalSpeedFpm >= MaxDescentFpm
               && Math.Abs(t.BankDeg) <= MaxBankDeg
               && onCentreline
               && !t.StallWarning && !t.OverspeedWarning;
        if (ok)
            _approachPass++;

        // A friendly nudge as you start to drift — BEFORE it costs the score (the Fun Dial, L9).
        if (xtrack > CoachApproachCrossTrackFt && !_localizerCoached)
        {
            _localizerCoached = true;
            string side = t.ApproachCrossTrackFt > 0 ? "right of" : "left of";
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching,
                $"Drifting {side} the centreline — small, early corrections back onto the approach"));
        }
    }

    // Enroute exceedances — each fires once, on first breach, as a scored event (Phase 7a streams and
    // persists it), and docks the enroute score. We trust the sim's own stall/overspeed annunciators
    // rather than guessing reference speeds (that stays true to what the sim reported).
    private void CheckViolations(TelemetrySnapshot t)
    {
        if (t.OverspeedWarning && !_overspeedWarned)
        {
            _overspeedWarned = true; _violationPoints += PtsOverspeed;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, "Overspeed warning"));
        }
        if (t.StallWarning && !_stallWarned)
        {
            _stallWarned = true; _violationPoints += PtsStall;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, "Stall warning"));
        }
        double bank = Math.Abs(t.BankDeg);
        if (bank > OverBankDeg && !_bankWarned)
        {
            _bankWarned = true; _violationPoints += PtsOverBank;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, $"Steep bank {bank:F0}°"));
        }
        // The Fun Dial (L9): a steep-ish bank that's short of the exceedance gets a nudge, not a penalty.
        else if (bank > CoachBankDeg && !_bankCoached && !_bankWarned)
        {
            _bankCoached = true;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching, $"Bank {bank:F0}° — ease it back"));
        }

        if ((t.GForce > OverGHigh || t.GForce < OverGLow) && !_gWarned)
        {
            _gWarned = true; _violationPoints += PtsOverG;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, $"High load {t.GForce:F1} g"));
        }
        else if ((t.GForce > CoachGHigh || t.GForce < CoachGLow) && !_gCoached && !_gWarned)
        {
            _gCoached = true;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching, $"Load {t.GForce:F1} g — smooth it out"));
        }
    }

    // Weight & balance at takeoff (Phase 9d), on the Fun Dial (L9). Both checks run ONLY when the sim reports
    // the limit (default 0 = not reported → nothing happens, L10). Comparisons are relative to the aircraft's
    // OWN limits, so they're correct whatever percent scale the sim uses.
    private void CheckWeightAndBalance(TelemetrySnapshot t)
    {
        if (t.MaxGrossWeightLbs > 0 && t.TotalWeightLbs > 0)
        {
            double overLbs = t.TotalWeightLbs - t.MaxGrossWeightLbs;
            if (t.TotalWeightLbs > t.MaxGrossWeightLbs * OverweightGrossFactor) // gross overload — a real, warned consequence
            {
                _violationPoints += PtsOverweight;
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning,
                    $"Overweight takeoff — {overLbs:F0} lb over MTOW"));
            }
            else if (overLbs > 0) // a hair over — a nudge, not a straf
            {
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching,
                    $"A touch over MTOW — {overLbs:F0} lb; watch the loading"));
            }
        }

        if (t.CgAftLimit > t.CgFwdLimit) // a real envelope reported
        {
            double margin = CgGrossEnvelopeFrac * (t.CgAftLimit - t.CgFwdLimit);
            bool grosslyOut = t.CgPercent > t.CgAftLimit + margin || t.CgPercent < t.CgFwdLimit - margin;
            bool outside = t.CgPercent > t.CgAftLimit || t.CgPercent < t.CgFwdLimit;
            if (grosslyOut)
            {
                _violationPoints += PtsCg;
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning,
                    "Out of CG limits — recheck the load sheet"));
            }
            else if (outside)
            {
                _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Coaching,
                    "CG near the edge of the envelope — trim will feel it"));
            }
        }
    }

    // Flight-integrity monitoring: slew anywhere airborne, or time-acceleration near the ground, voids the
    // score (the approach and touchdown are what we grade, so those are the windows we protect). Enroute
    // time-compression on a long ferry is a legitimate workflow and is deliberately left alone.
    private void CheckIntegrity(TelemetrySnapshot t)
    {
        if (!_scoreValid)
            return;
        double agl = t.AltitudeAglFt > 0 ? t.AltitudeAglFt : t.AltitudeFt;
        string? reason = t.SlewActive ? "Slew detected — score void"
                       : t.SimRate > 1.0 && agl < GateAglFt ? "Time acceleration near the ground — score void"
                       : null;
        if (reason is not null)
        {
            _scoreValid = false;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, reason));
        }
    }

    private int ApproachScore()
        => _approachTotal > 0 ? (int)Math.Round(100.0 * _approachPass / _approachTotal) : 100;

    private void Complete()
    {
        double worst3 = _worstFpm != 0 ? _worstFpm : _touchdownFpm;
        int rawLanding = Math.Min(FpmScore(worst3), Math.Min(GScore(_worstG), BankScore(_worstBank)));
        // The sim's actual conditions at touchdown lift the grade: crosswind = |wind · sin(windDir − heading)|,
        // plus low visibility. A firm arrival in tough air was better flying than the raw numbers suggest.
        double crosswind = Math.Abs(_tdWindKts * Math.Sin((_tdWindDir - _tdHeading) * Math.PI / 180.0));
        int weatherBonus = WeatherLandingBonus(crosswind, _tdVisSm);
        int landing = Math.Min(100, rawLanding + weatherBonus);
        if (weatherBonus > 0)
            _events.Add(new FlightEvent(_arrivedAt, FlightEventSeverity.Info,
                $"Tough conditions — {crosswind:F0} kt crosswind{(_tdVisSm < VisEasySm ? $", {_tdVisSm:F1} sm vis" : "")} — +{weatherBonus} to the landing grade"));
        int approach = ApproachScore();
        int enroute = Math.Clamp(100 - _violationPoints, 0, 100);
        int overall = (int)Math.Round(0.55 * landing + 0.30 * approach + 0.15 * enroute);
        int comfort = ComfortScore(_peakBankDeg, _peakGDev, worst3, _worstG, _violationPoints); // Phase 10c

        Result = new FlightRecord(
            _title, _departedAt, _arrivedAt, _touchdownFpm, _maxAltFt,
            _depLat, _depLon, _arrLat, _arrLon,
            GeoMath.DistanceNm(_depLat, _depLon, _arrLat, _arrLon),
            Math.Max(0, _depFuel - _arrFuel),
            _events.ToList())
        {
            TouchdownFpmWorst3 = worst3,
            TouchdownG = _worstG,
            TouchdownBankDeg = _worstBank,
            LandingScore = landing,
            ApproachScore = approach,
            EnrouteScore = enroute,
            OverallScore = overall,
            ComfortScore = comfort,
            StabilizedApproach = approach >= StableApproachMinScore,
            ViolationPoints = _violationPoints,
            Scored = true,
            ScoreValid = _scoreValid,
            EngineDamagePctAccrued = EngineDamageAccrued(), // Phase 9e — priced into engine wear at settlement
        };
        _inFlight = false;
        _landed = false;
    }

    private static FlightEventSeverity LandingSeverity(double fpm)
    {
        double m = Math.Abs(fpm);
        return m <= 200 ? FlightEventSeverity.Success
             : m <= 600 ? FlightEventSeverity.Info
             : FlightEventSeverity.Warning;
    }

    // Sub-scores — a landing is only as good as its worst axis (min of the three).
    private static int FpmScore(double fpm)
    {
        double m = Math.Abs(fpm);
        return m <= 60 ? 100 : m <= 150 ? 90 : m <= 240 ? 75 : m <= 400 ? 55 : m <= 600 ? 35 : m <= 1000 ? 10 : 0;
    }

    private static int GScore(double g)
    {
        double m = Math.Abs(g);
        return m <= 1.2 ? 100 : m <= 1.4 ? 90 : m <= 1.8 ? 75 : m <= 2.2 ? 55 : m <= 2.8 ? 35 : m <= 3.5 ? 10 : 0;
    }

    private static int BankScore(double bankDeg)
    {
        double m = Math.Abs(bankDeg);
        return m <= 3 ? 100 : m <= 6 ? 90 : m <= 10 ? 75 : m <= 15 ? 55 : m <= 25 ? 30 : 10;
    }

    // Passenger comfort (Phase 10c): a limousine ride starts at 100 and loses points for what a passenger
    // actually feels — steep banks, a bumpy g envelope, a firm touchdown, and any hard exceedance. Purely a
    // read of the recorded envelope (un-gameable); the economy turns it into a comfort tip on a passenger leg.
    private static int ComfortScore(double peakBankDeg, double peakGDev, double touchdownFpm, double touchdownG, int violationPoints)
    {
        double penalty = 1.5 * Math.Max(0, peakBankDeg - 20)             // banks past ~20° are felt in the seat
                       + 80 * Math.Max(0, peakGDev - 0.25)               // a bumpy ride — g wandering past ±0.25
                       + Math.Max(0, Math.Abs(touchdownFpm) - 200) / 8.0 // a firm arrival
                       + 40 * Math.Max(0, touchdownG - 1.3)              // the thump at contact
                       + 6 * violationPoints;                            // every exceedance rattles them
        return (int)Math.Clamp(100 - Math.Round(penalty), 0, 100);
    }

    // Grade points the conditions earn back: crosswind (weighted 0.7) + low visibility (0.3), scaled to the cap.
    // Calm and clear (the default telemetry) returns 0, so a leg flown without weather data is graded as before.
    internal static int WeatherLandingBonus(double crosswindKts, double visSm)
    {
        double xw = Math.Clamp(crosswindKts / XwindFullKts, 0, 1);
        double vis = Math.Clamp((VisEasySm - visSm) / (VisEasySm - VisHardSm), 0, 1);
        double difficulty = Math.Clamp(xw * 0.7 + vis * 0.3, 0, 1);
        return (int)Math.Round(difficulty * MaxWeatherLandingBonus);
    }
}
