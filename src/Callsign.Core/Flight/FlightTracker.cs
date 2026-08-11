using Callsign.Core.Geo;
using Callsign.SimConnect;

namespace Callsign.Core.Flight;

/// <summary>
/// A state machine that consumes the telemetry stream and records a flight: the phase transitions,
/// the touchdown vertical speed (the headline score), block time, distance, fuel burn, and a log of
/// scored events. Feed it snapshots one at a time via <see cref="Observe"/> (in the app, wire it to
/// <c>ISimTelemetrySource.TelemetryReceived</c>); it is pure and deterministic, so scripted telemetry
/// tests it without a sim. It never throws on odd input — worst case it simply doesn't complete.
/// </summary>
public sealed class FlightTracker
{
    private const double TaxiSpeedKts = 3;       // above this, on the ground = taxiing
    private const double TaxiOverspeedKts = 30;  // brief §2.3 rough-handling: fast taxi is penalised
    private const double ClimbVsFpm = 200;
    private const double DescentVsFpm = -200;

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
                // airborne again after a touchdown: a go-around / bounce, not the final landing
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
    }

    private void ObserveOnGround(TelemetrySnapshot t)
    {
        if (_wasAirborne && _inFlight)
        {
            // touchdown: the descent rate on the last airborne sample is the touchdown rate
            _touchdownFpm = _lastAirborneVs;
            _arrivedAt = t.CapturedAt;
            _arrLat = t.LatitudeDeg;
            _arrLon = t.LongitudeDeg;
            _arrFuel = t.FuelQuantityLbs;
            _landed = true;
            Phase = FlightPhase.Landing;
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

    private void Complete()
    {
        Result = new FlightRecord(
            _title, _departedAt, _arrivedAt, _touchdownFpm, _maxAltFt,
            _depLat, _depLon, _arrLat, _arrLon,
            GeoMath.DistanceNm(_depLat, _depLon, _arrLat, _arrLon),
            Math.Max(0, _depFuel - _arrFuel),
            _events.ToList());
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
}
