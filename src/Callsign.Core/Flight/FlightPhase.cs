namespace Callsign.Core.Flight;

/// <summary>Where a tracked flight is in its lifecycle (brief §5.2).</summary>
public enum FlightPhase
{
    Parked,
    Taxi,
    Takeoff,
    Climb,
    Cruise,
    Approach,
    Landing,
    Shutdown,
}
