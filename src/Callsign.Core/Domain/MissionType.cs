namespace Callsign.Core.Domain;

/// <summary>The kind of job. Pinned values (persisted); only Cargo is generated in Phase 1.</summary>
public enum MissionType
{
    Cargo = 1,
    Passenger = 2,
    Express = 3,
    Sensitive = 4,
    Hazardous = 5,
    Emergency = 6,
    SearchAndRescue = 7,
    Tourist = 8,
    Parachute = 9,
    Vip = 10,
    Illicit = 11,
}
