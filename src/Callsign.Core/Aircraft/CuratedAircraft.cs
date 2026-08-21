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
        // Starter-pick trio (Phase 12 onboarding): the C152 above plus these two. Specs approximate (retunable).
        new("DR40", "Robin DR400", "Robin", AircraftCategory.LightSingle, 4, 838, 190, 130, 1400, ["Robin DR400", "DR400", "DR 400", "Robin DR400-140B", "DR400-140B", "Robin DR401"]),
        new("VL3", "JMB VL3", "JMB", AircraftCategory.LightSingle, 2, 550, 180, 150, 1000, ["JMB VL3", "VL-3", "VL3", "JMB Aircraft VL-3", "JMB VL-3 915", "VL3 915"]),
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

        // ── Fleet expansion (Phase 6c): common airliners, GA, bizjets, turboprops, helis, warbirds, and
        //    popular addon types. Specs are approximate real-world figures (retunable). ──
        new("A319", "Airbus A319", "Airbus", AircraftCategory.Heavy, 140, 38000, 42000, 447, 6000, ["A319"]),
        new("A21N", "Airbus A321neo", "Airbus", AircraftCategory.Heavy, 220, 48000, 46000, 450, 7000, ["A321neo", "Airbus A321neo"]),
        new("B738", "Boeing 737-800", "Boeing", AircraftCategory.Heavy, 189, 46000, 46000, 453, 7000, ["Boeing 737-800", "737-800"]),
        new("B38M", "Boeing 737 MAX 8", "Boeing", AircraftCategory.Heavy, 178, 46000, 46000, 453, 7000, ["Boeing 737 MAX 8", "737 MAX 8"]),
        new("B77W", "Boeing 777-300ER", "Boeing", AircraftCategory.Heavy, 396, 240000, 320000, 490, 10500, ["Boeing 777-300ER", "777-300ER"]),
        new("B789", "Boeing 787-9 Dreamliner", "Boeing", AircraftCategory.Heavy, 296, 165000, 223000, 488, 9000, ["Boeing 787-9", "Dreamliner"]),
        new("A359", "Airbus A350-900", "Airbus", AircraftCategory.Heavy, 325, 180000, 240000, 488, 8500, ["Airbus A350-900", "A350"]),
        new("A388", "Airbus A380-800", "Airbus", AircraftCategory.Heavy, 555, 640000, 560000, 490, 10000, ["Airbus A380", "A380-800"]),
        new("E75L", "Embraer E175", "Embraer", AircraftCategory.Jet, 76, 22000, 20000, 447, 5100, ["Embraer E175", "E175"]),
        new("CRJ7", "Bombardier CRJ700", "Bombardier", AircraftCategory.Jet, 70, 19000, 19500, 447, 5400, ["CRJ700", "CRJ-700"]),
        new("AT76", "ATR 72-600", "ATR", AircraftCategory.Turboprop, 72, 16000, 11000, 276, 4200, ["ATR 72", "ATR72"]),
        new("DH8D", "De Havilland Dash 8 Q400", "De Havilland", AircraftCategory.Turboprop, 78, 18000, 11700, 360, 4300, ["Dash 8", "Q400"]),

        new("P28A", "Piper PA-28 Archer", "Piper", AircraftCategory.LightSingle, 4, 900, 300, 128, 1600, ["PA-28", "Archer", "Cherokee"]),
        new("PA18", "Piper PA-18 Super Cub", "Piper", AircraftCategory.LightSingle, 2, 500, 216, 100, 500, ["Super Cub", "PA-18"]),
        new("C182", "Cessna 182 Skylane", "Cessna", AircraftCategory.LightSingle, 4, 1100, 522, 145, 1600, ["Cessna 182", "Skylane"]),
        new("C206", "Cessna 206 Stationair", "Cessna", AircraftCategory.LightSingle, 6, 1400, 546, 151, 1800, ["Cessna 206", "Stationair"]),
        new("M20P", "Mooney M20", "Mooney", AircraftCategory.LightSingle, 4, 1000, 384, 175, 2000, ["Mooney M20", "Mooney"]),
        new("SR20", "Cirrus SR20", "Cirrus", AircraftCategory.LightSingle, 5, 950, 336, 155, 1700, ["Cirrus SR20", "SR20"]),

        new("BE20", "Beechcraft King Air 200", "Beechcraft", AircraftCategory.Turboprop, 13, 4400, 3645, 289, 3200, ["King Air 200", "B200"]),
        new("DHC6", "De Havilland DHC-6 Twin Otter", "De Havilland", AircraftCategory.Turboprop, 19, 4200, 2500, 150, 1200, ["Twin Otter", "DHC-6"]),
        new("PC6", "Pilatus PC-6 Porter", "Pilatus", AircraftCategory.Turboprop, 10, 2500, 1500, 130, 640, ["PC-6", "Porter"]),
        new("AT802", "Air Tractor AT-802", "Air Tractor", AircraftCategory.Turboprop, 2, 8000, 2400, 190, 2000, ["AT-802", "Air Tractor"]),

        new("SF50", "Cirrus Vision Jet", "Cirrus", AircraftCategory.LightJet, 5, 1200, 2000, 300, 1400, ["Vision Jet", "SF50", "Cirrus Vision Jet"]),
        new("E55P", "Embraer Phenom 300", "Embraer", AircraftCategory.LightJet, 9, 2400, 5353, 453, 3138, ["Phenom 300", "Phenom 300E"]),
        new("LJ45", "Learjet 45", "Learjet", AircraftCategory.Jet, 9, 3600, 6062, 464, 5040, ["Learjet 45", "LJ45"]),
        new("GLF6", "Gulfstream G650", "Gulfstream", AircraftCategory.Jet, 19, 6000, 44200, 516, 6000, ["G650", "Gulfstream G650"]),
        new("CL60", "Bombardier Challenger 650", "Bombardier", AircraftCategory.Jet, 12, 5000, 20000, 470, 5640, ["Challenger 650", "CL60"]),

        new("B06", "Bell 206 JetRanger", "Bell", AircraftCategory.Helicopter, 5, 1500, 480, 120, 0, ["Bell 206", "JetRanger"]),
        new("B407", "Bell 407", "Bell", AircraftCategory.Helicopter, 7, 2300, 900, 133, 0, ["Bell 407"]),
        new("H145", "Airbus H145", "Airbus", AircraftCategory.Helicopter, 10, 3800, 1400, 130, 0, ["H145", "EC145"]),
        new("R44", "Robinson R44", "Robinson", AircraftCategory.Helicopter, 4, 900, 180, 109, 0, ["Robinson R44", "R44"]),

        new("BE60", "Beechcraft Duke B60", "Beechcraft", AircraftCategory.LightTwin, 6, 1800, 918, 220, 2200, ["Duke", "B60 Duke"]),
        new("BN2A", "Britten-Norman BN-2 Islander", "Britten-Norman", AircraftCategory.LightTwin, 9, 2000, 1200, 150, 1200, ["BN-2 Islander", "Islander"]),
        new("F117", "Lockheed F-117 Nighthawk", "Lockheed", AircraftCategory.Jet, 1, 5000, 18000, 480, 5000, ["F-117", "Nighthawk", "F-117A"]),
        new("F22", "Lockheed Martin F-22 Raptor", "Lockheed Martin", AircraftCategory.Jet, 2, 8000, 18000, 500, 5000, ["F-22", "Raptor", "F-22 Raptor"]),
        new("SPIT", "Supermarine Spitfire", "Supermarine", AircraftCategory.LightSingle, 1, 1200, 620, 330, 2000, ["Spitfire"]),
        new("P51", "North American P-51 Mustang", "North American", AircraftCategory.LightSingle, 1, 2000, 1500, 362, 2500, ["P-51", "Mustang", "P-51D"]),
    ];
}
