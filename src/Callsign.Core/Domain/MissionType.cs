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

public static class MissionTypeExtensions
{
    /// <summary>Missions that carry people, so "the right aircraft" is measured in SEATS, not payload
    /// weight. Settlement and the UI branch on this to gate the capability bonus correctly.</summary>
    public static bool CarriesPassengers(this MissionType type)
        => type is MissionType.Passenger or MissionType.Vip or MissionType.Tourist;
}
