using Callsign.Core.Aircraft;
using Callsign.Core.Domain;
using Xunit;

namespace Callsign.Core.Tests;

public class AircraftScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"callsign-scan-{Guid.NewGuid():N}");

    private const string Pc12Cfg =
        "[GENERAL]\n" +
        "Category = \"Airplane\"\n" +
        "icao_type_designator = \"PC12\"\n" +
        "icao_manufacturer = \"PILATUS\"\n" +
        "[FLTSIM.0]\n" +
        "Title=\"Pilatus PC-12/47 OH-JEM Executive\"\n" +
        "ui_manufacturer=\"Pilatus\"\n" +
        "ui_type=\"PC-12/47, 4-bladed\"\n" +
        "ui_typerole=\"Single Engine Turboprop\"\n" +
        "[FLTSIM.1]\n" +
        "Title=\"Pilatus PC-12/47 White Executive\"\n";

    private void PlaceAircraft(string root, string package, string aircraft, string cfg)
    {
        var dir = Path.Combine(_root, root, package, "SimObjects", "Airplanes", aircraft);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "aircraft.cfg"), cfg);
    }

    [Fact]
    public void Scan_GroupsLiveries_IntoOneTypeWithAliasesAndSource()
    {
        PlaceAircraft("Community2024", "sws-aircraft-pc12", "SWS_PC12_4B", Pc12Cfg);

        var roster = new AircraftScanner().Scan(_root);

        var type = Assert.Single(roster);
        Assert.Equal("PC12", type.Key);
        Assert.Equal("Pilatus PC-12/47, 4-bladed", type.CanonicalName);
        Assert.Equal(AircraftCategory.Turboprop, type.Category);
        Assert.Equal(2, type.Titles.Count);
        Assert.Contains("Pilatus PC-12/47 OH-JEM Executive", type.Titles);
        var loc = Assert.Single(type.Locations);
        Assert.Equal("Community2024", loc.Source);
        Assert.Equal("sws-aircraft-pc12", loc.PackageFolder);
        Assert.Equal("SWS_PC12_4B", loc.AircraftFolder);
    }

    [Fact]
    public void Scan_MergesSameIcaoAcrossPackages()
    {
        PlaceAircraft("Community2024", "pc12-a", "PC12_4B", Pc12Cfg);
        PlaceAircraft("Community2024", "pc12-b", "PC12_5B", Pc12Cfg.Replace("OH-JEM Executive", "Registry Two"));

        var roster = new AircraftScanner().Scan(_root);

        var type = Assert.Single(roster);          // same icao PC12 -> one type
        Assert.Equal(2, type.Locations.Count);     // two packages
        Assert.Equal(3, type.Titles.Count);        // union of liveries
    }

    [Fact]
    public void Scan_FallsBackFromLocKeysToIcao_AndIgnoresCommentsForCategory()
    {
        var cfg =
            "[GENERAL]\nicao_type_designator=\"DR40\"\nicao_manufacturer=\"ROBIN\"\nicao_model=\"DR400\"\n" +
            "[FLTSIM.0]\nTitle=\"Robin DR400\"\n" +
            "ui_manufacturer=\"TT:AIRCRAFT.UI_MANUFACTURER\" ; e.g. Boeing\n" +
            "ui_typerole=\"TT:AIRCRAFT.UI_TYPEROLE\" ; e.g. rotorcraft\n";
        PlaceAircraft("Community2024", "dr400", "DR400", cfg);

        var type = Assert.Single(new AircraftScanner().Scan(_root));
        Assert.Equal("DR40", type.Key);
        Assert.DoesNotContain("TT:", type.CanonicalName);        // loc key not used as a name
        Assert.NotEqual(AircraftCategory.Helicopter, type.Category); // "rotorcraft" was only in a comment
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
