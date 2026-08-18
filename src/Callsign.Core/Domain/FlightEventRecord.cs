namespace Callsign.Core.Domain;

/// <summary>
/// A persisted scored moment from a flight — takeoff, the touchdown and its quality, a taxi overspeed.
/// These are the real events <see cref="Callsign.Core.Flight.FlightTracker"/> produces while the leg is
/// flown; Phase 7a writes them in the SAME transaction as the settled <see cref="Flight"/> row so the
/// logbook can replay the running story of a flight after the fact, and streams the same events live
/// over the telemetry socket. Before 7a these events were computed and thrown away, and the in-flight
/// log was fabricated on the client — this entity is what makes that story true and durable.
/// </summary>
public sealed class FlightEventRecord
{
    public long Id { get; set; }               // autoincrement — the natural chronological order key
    public Guid FlightId { get; set; }         // the settled flight this moment belongs to
    public int Seq { get; set; }               // order within the flight (events can share a timestamp)
    public DateTimeOffset At { get; set; }
    public string Severity { get; set; } = null!;  // FlightEventSeverity name: Info | Success | Warning
    public string Message { get; set; } = null!;
}
