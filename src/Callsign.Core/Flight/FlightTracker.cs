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
    private const double OverBankDeg = 60;        // an enroute over-bank exceedance
    private const double OverGHigh = 2.5, OverGLow = -1.0;
    private const int PtsOverspeed = 15, PtsStall = 20, PtsOverBank = 10, PtsOverG = 15;

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
    private readonly List<double> _recentVs = []; // last ≤3 airborne descent samples (reset on go-around)
    private double _lastAirborneBankDeg;
    private double _lastAirborneG = 1.0;
    private double _touchdownG = 1.0;
    private double _touchdownBankDeg;
    private int _approachPass, _approachTotal;
    private int _violationPoints;
    private bool _overspeedWarned, _stallWarned, _bankWarned, _gWarned;
    private bool _scoreValid = true; // flight-integrity monitoring (Phase 7c anti-cheat)

    public FlightPhase Phase { get; private set; } = FlightPhase.Parked;
    public IReadOnlyList<FlightEvent> Events => _events;

    /// <summary>Set once the flight is complete (landed, then stopped). Null until then.</summary>
    public FlightRecord? Result { get; private set; }

    public void Observe(TelemetrySnapshot t)
    {
        _title = t.AircraftTitle;
        bool airborne = !t.OnGround;

        if (airborne)
            ObserveAirborne(t);
        else
            ObserveOnGround(t);

        _wasAirborne = airborne;
    }

    private void ObserveAirborne(TelemetrySnapshot t)
    {
        _maxAltFt = Math.Max(_maxAltFt, t.AltitudeFt);
        _lastAirborneVs = t.VerticalSpeedFpm;
        _lastAirborneBankDeg = Math.Abs(t.BankDeg);
        _lastAirborneG = Math.Abs(t.GForce);

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
            }
            else
            {
                // airborne again after a touchdown: a go-around / bounce, not the final landing.
                // The next approach is graded fresh, so forget the previous approach's flare samples.
                _landed = false;
            }
            _recentVs.Clear();
            Phase = FlightPhase.Takeoff;
        }
        else
        {
            Phase = t.VerticalSpeedFpm > ClimbVsFpm ? FlightPhase.Climb
                  : t.VerticalSpeedFpm < DescentVsFpm ? FlightPhase.Approach
                  : FlightPhase.Cruise;
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
            // touchdown: the descent rate on the last airborne sample is the raw touchdown rate; the
            // grade uses the worst of the last three (computed in Complete), which a soft frame can't game.
            _touchdownFpm = _lastAirborneVs;
            _touchdownG = Math.Max(Math.Abs(t.GForce), _lastAirborneG); // impact load, or the last flown load
            _touchdownBankDeg = _lastAirborneBankDeg;
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
        bool ok = t.VerticalSpeedFpm >= MaxDescentFpm
               && Math.Abs(t.BankDeg) <= MaxBankDeg
               && !t.StallWarning && !t.OverspeedWarning;
        if (ok)
            _approachPass++;
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
        if ((t.GForce > OverGHigh || t.GForce < OverGLow) && !_gWarned)
        {
            _gWarned = true; _violationPoints += PtsOverG;
            _events.Add(new FlightEvent(t.CapturedAt, FlightEventSeverity.Warning, $"High load {t.GForce:F1} g"));
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
        double worst3 = _recentVs.Count > 0 ? _recentVs.Min() : _touchdownFpm;
        int landing = Math.Min(FpmScore(worst3), Math.Min(GScore(_touchdownG), BankScore(_touchdownBankDeg)));
        int approach = ApproachScore();
        int enroute = Math.Clamp(100 - _violationPoints, 0, 100);
        int overall = (int)Math.Round(0.55 * landing + 0.30 * approach + 0.15 * enroute);

        Result = new FlightRecord(
            _title, _departedAt, _arrivedAt, _touchdownFpm, _maxAltFt,
            _depLat, _depLon, _arrLat, _arrLon,
            GeoMath.DistanceNm(_depLat, _depLon, _arrLat, _arrLon),
            Math.Max(0, _depFuel - _arrFuel),
            _events.ToList())
        {
            TouchdownFpmWorst3 = worst3,
            TouchdownG = _touchdownG,
            TouchdownBankDeg = _touchdownBankDeg,
            LandingScore = landing,
            ApproachScore = approach,
            EnrouteScore = enroute,
            OverallScore = overall,
            StabilizedApproach = approach >= StableApproachMinScore,
            ViolationPoints = _violationPoints,
            Scored = true,
            ScoreValid = _scoreValid,
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
}
