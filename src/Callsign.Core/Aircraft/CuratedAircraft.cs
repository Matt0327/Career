using Callsign.Core.Domain;

namespace Callsign.Core.Aircraft;

/// <summary>A curated aircraft entry: identity plus the physical specs aircraft.cfg does not expose.</summary>
public sealed record CuratedAircraft(
    string IcaoTypeDesignator,
    string CanonicalName,
    string Manufacturer,
    AircraftCategory Category,
    int Seats,
    int UsefulLoadLbs,
    int FuelCapacityLbs,
    int CruiseKtas,
    int MinRunwayFt,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Curated fallback catalog of common MSFS 2024 default-fleet aircraft (brief §4). MSFS 2024 streams
/// the Official fleet from the cloud, so these have no on-disk aircraft.cfg to scan; this table
/// supplies their identity AND the physical specs (seats, useful load, fuel, cruise, runway) that
/// aircraft.cfg never exposes. Values are approximate real-world figures and are retunable; a scanned
/// community aircraft that shares an ICAO type designator is enriched with these specs. This is a
/// starting set, not the whole fleet, and is a natural candidate to become data-driven later.
/// </summary>
public static class DefaultFleetCatalog
{
    public static IReadOnlyList<CuratedAircraft> Aircraft2024 { get; } =
    [
        new("C152", "Cessna 152", "Cessna", AircraftCategory.LightSingle, 2, 520, 160, 107, 1500, ["Cessna 152 Asobo", "Cessna 152"]),
        new("C172", "Cessna 172 Skyhawk", "Cessna", AircraftCategory.LightSingle, 4, 878, 336, 124, 1600, ["Cessna Skyhawk G1000 Asobo", "Cessna 172 Skyhawk G1000", "Cessna 172 Skyhawk"]),
        new("DA40", "Diamond DA40 NG", "Diamond", AircraftCategory.LightSingle, 4, 838, 200, 150, 1700, ["Diamond DA40-NG Asobo", "DA40 NG"]),
        new("SR22", "Cirrus SR22", "Cirrus", AircraftCategory.LightSingle, 4, 1150, 552, 180, 1600, ["Cirrus SR22 G6 Asobo", "Cirrus SR22"]),
        new("BE36", "Beechcraft Bonanza G36", "Beechcraft", AircraftCategory.LightSingle, 5, 1050, 444, 176, 1800, ["Bonanza G36 Asobo", "Beechcraft Bonanza G36"]),
        new("BE58", "Beechcraft Baron G58", "Beechcraft", AircraftCategory.LightTwin, 6, 1700, 996, 200, 2300, ["Baron G58 Asobo", "Beechcraft Baron G58"]),
        new("DA62", "Diamond DA62", "Diamond", AircraftCategory.LightTwin, 7, 1323, 530, 192, 1700, ["Diamond DA62 Asobo", "DA62"]),
        new("C208", "Cessna 208B Grand Caravan", "Cessna", AircraftCategory.Turboprop, 9, 3305, 2245, 186, 2000, ["Cessna 208B Grand Caravan EX Asobo", "Grand Caravan"]),
        new("TBM9", "Daher TBM 930", "Daher", AircraftCategory.Turboprop, 6, 1450, 1950, 330, 2400, ["TBM 930 Asobo", "Daher TBM 930"]),
        new("PC12", "Pilatus PC-12", "Pilatus", AircraftCategory.Turboprop, 9, 2100, 2704, 270, 2600, ["Pilatus PC-12", "PC-12 NGX"]),
        new("B350", "Beechcraft King Air 350i", "Beechcraft", AircraftCategory.Turboprop, 11, 5145, 3645, 312, 3300, ["King Air 350i Asobo", "Beechcraft King Air 350i"]),
        new("C25C", "Cessna Citation CJ4", "Cessna", AircraftCategory.LightJet, 9, 2200, 5828, 451, 3410, ["Cessna Citation CJ4 Asobo", "Citation CJ4"]),
        new("C68A", "Cessna Citation Longitude", "Cessna", AircraftCategory.Jet, 12, 3600, 14200, 476, 4000, ["Cessna Citation Longitude Asobo", "Citation Longitude"]),
        new("A20N", "Airbus A320neo", "Airbus", AircraftCategory.Heavy, 180, 42000, 42000, 450, 6500, ["Airbus A320neo Asobo", "A320neo"]),
        new("B748", "Boeing 747-8i", "Boeing", AircraftCategory.Heavy, 410, 300000, 420000, 490, 9500, ["Boeing 747-8i Asobo", "747-8"]),
        new("H125", "Airbus H125", "Airbus", AircraftCategory.Helicopter, 6, 2500, 1200, 120, 0, ["Airbus H125 Asobo", "H125"]),
    ];
}
