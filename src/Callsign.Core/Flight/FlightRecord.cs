namespace Callsign.Core.Flight;

/// <summary>Severity of a scored flight event, for the live event log.</summary>
public enum FlightEventSeverity
{
    Info,
    Success,
    Warning,
}

/// <summary>A timestamped, scored moment during a flight (takeoff, a taxi overspeed, the touchdown).</summary>
public sealed record FlightEvent(DateTimeOffset At, FlightEventSeverity Severity, string Message);

/// <summary>
/// The raw result of tracking one flight, produced by <see cref="FlightTracker"/>. The economy
/// settles this into an itemised payout (Phase 1f); this record holds only what was observed.
/// </summary>
public sealed record FlightRecord(
    string AircraftTitle,
    DateTimeOffset DepartedAt,
    DateTimeOffset ArrivedAt,
    double TouchdownFpm,
    double MaxAltitudeFt,
    double DepartureLat,
    double DepartureLon,
    double ArrivalLat,
    double ArrivalLon,
    double DistanceNm,
    double FuelUsedLbs,
    IReadOnlyList<FlightEvent> Events)
{
    public TimeSpan BlockTime => ArrivedAt - DepartedAt;
}
