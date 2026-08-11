using Callsign.Core.Domain;

namespace Callsign.Core.Aircraft;

/// <summary>Where a scanned aircraft came from on disk.</summary>
public sealed record ScannedLocation(string Source, string PackageFolder, string AircraftFolder);

/// <summary>A recognised aircraft type from the scan, with all its observed liveries and locations.</summary>
public sealed record ScannedAircraftType(
    string Key,
    string CanonicalName,
    string? Manufacturer,
    string? IcaoTypeDesignator,
    string? IcaoModel,
    AircraftCategory Category,
    string? UiTypeRole,
    IReadOnlyList<string> Titles,
    IReadOnlyList<ScannedLocation> Locations);

/// <summary>
/// Scans the sim's on-disk packages for aircraft and groups their liveries into recognised types.
///
/// NOTE: MSFS 2024 streams the default (Official) fleet from the cloud, so those aircraft have NO
/// on-disk aircraft.cfg — this finds Community / on-disk aircraft. The default fleet is supplied by a
/// bundled curated catalog (added later). To stay fast it only descends into <c>SimObjects/Airplanes</c>
/// folders rather than crawling entire (streamed) package trees.
/// </summary>
public sealed class AircraftScanner
{
    private static readonly string[] PackageRoots = ["Official2024", "Community2024", "Official2020", "Community"];

    public IReadOnlyList<ScannedAircraftType> Scan(string installedPackagesPath)
    {
        var raw = new List<Raw>();
        foreach (var root in PackageRoots)
        {
            var rootDir = Path.Combine(installedPackagesPath, root);
            if (!Directory.Exists(rootDir))
                continue;
            foreach (var cfgPath in EnumerateAircraftCfgs(rootDir))
            {
                var parsed = ParseOne(cfgPath, root);
                if (parsed is not null)
                    raw.Add(parsed);
            }
        }

        return raw
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(grp =>
            {
                var first = grp.First();
                var titles = grp.SelectMany(x => x.Titles)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                var locations = grp.Select(x => x.Location).ToList();
                return new ScannedAircraftType(first.Key, first.CanonicalName, first.Manufacturer,
                    first.IcaoTypeDesignator, first.IcaoModel, first.Category, first.UiTypeRole, titles, locations);
            })
            .OrderBy(t => t.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record Raw(
        string Key, string CanonicalName, string? Manufacturer, string? IcaoTypeDesignator,
        string? IcaoModel, AircraftCategory Category, string? UiTypeRole,
        IReadOnlyList<string> Titles, ScannedLocation Location);

    private static Raw? ParseOne(string cfgPath, string source)
    {
        AircraftCfg cfg;
        try
        {
            using var reader = new StreamReader(cfgPath);
            cfg = AircraftCfg.Parse(reader);
        }
        catch
        {
            return null;
        }

        var titles = cfg.FltSims
            .Select(fs => Get(fs, "title"))
            .Where(s => s is not null).Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
            return null;

        var g = cfg.General;
        var first = cfg.FltSims.Count > 0 ? cfg.FltSims[0] : null;

        string? icao = Get(g, "icao_type_designator");
        string? manufacturer = Get(first, "ui_manufacturer") ?? Get(g, "icao_manufacturer");
        string? uiType = Get(first, "ui_type") ?? Get(g, "icao_model");
        string? role = Get(first, "ui_typerole");

        string canonical = string.Join(" ", new[] { manufacturer, uiType }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(canonical))
            canonical = icao ?? titles[0];

        string key = !string.IsNullOrWhiteSpace(icao) ? icao!.ToUpperInvariant() : AircraftTitle.Normalize(canonical);
        if (string.IsNullOrWhiteSpace(key))
            key = AircraftTitle.Normalize(titles[0]);

        var location = new ScannedLocation(source, PackageFolderOf(source, cfgPath), AircraftFolderOf(cfgPath));
        return new Raw(key, canonical, manufacturer, icao, Get(g, "icao_model"), CategoryFromRole(role), role, titles, location);
    }

    private static IEnumerable<string> EnumerateAircraftCfgs(string rootDir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        foreach (var airplanesDir in AirplaneDirs(rootDir))
            foreach (var cfg in Directory.EnumerateFiles(airplanesDir, "aircraft.cfg", options))
                yield return cfg;
    }

    // Only descend into <package>/SimObjects/Airplanes (one or two levels deep), never the whole tree.
    private static IEnumerable<string> AirplaneDirs(string rootDir)
    {
        foreach (var lvl1 in SafeDirs(rootDir))
        {
            var a1 = Path.Combine(lvl1, "SimObjects", "Airplanes");
            if (Directory.Exists(a1)) { yield return a1; continue; }
            foreach (var lvl2 in SafeDirs(lvl1))
            {
                var a2 = Path.Combine(lvl2, "SimObjects", "Airplanes");
                if (Directory.Exists(a2))
                    yield return a2;
            }
        }
    }

    private static IReadOnlyList<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static string? Get(IReadOnlyDictionary<string, string>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v))
            return null;
        v = v.Trim();
        // Skip empty values and unresolved localisation keys (e.g. "TT:AIRCRAFT.UI_MANUFACTURER").
        return v.Length == 0 || v.StartsWith("TT:", StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    private static string PackageFolderOf(string source, string cfgPath) => SegmentAfter(cfgPath, source);
    private static string AircraftFolderOf(string cfgPath) => SegmentAfter(cfgPath, "Airplanes");

    private static string SegmentAfter(string path, string marker)
    {
        var parts = path.Replace('\\', '/').Split('/');
        int idx = Array.FindIndex(parts, p => p.Equals(marker, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : "";
    }

    private static AircraftCategory CategoryFromRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return AircraftCategory.Unknown;

        var r = role.ToLowerInvariant();
        if (r.Contains("helicopter") || r.Contains("rotor")) return AircraftCategory.Helicopter;
        if (r.Contains("glider") || r.Contains("sailplane")) return AircraftCategory.Glider;
        if (r.Contains("turboprop")) return AircraftCategory.Turboprop;
        if (r.Contains("jet")) return r.Contains("airliner") || r.Contains("heavy") ? AircraftCategory.Heavy : AircraftCategory.LightJet;
        if (r.Contains("airliner") || r.Contains("heavy")) return AircraftCategory.Heavy;
        if (r.Contains("twin") || r.Contains("multi")) return AircraftCategory.LightTwin;
        if (r.Contains("single")) return AircraftCategory.LightSingle;
        return AircraftCategory.Other;
    }
}
