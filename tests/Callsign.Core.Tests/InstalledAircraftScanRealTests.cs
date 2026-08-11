using Callsign.Core.Aircraft;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

// Runs the scanner against the REAL MSFS install on this machine when present, merges it with the
// curated default fleet, and dumps the roster to %TEMP%/callsign-roster-merged.txt for inspection.
// On CI (no sim) it no-ops.
public class InstalledAircraftScanRealTests
{
    [Fact]
    public async Task RealInstall_WhenPresent_ScanMergedWithCuratedFleet()
    {
        if (!MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp))
            return; // no MSFS here — nothing to verify

        var scan = new AircraftScanner().Scan(ipp);

        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        await new AircraftRosterService(db, new FakeClock()).RebuildAsync(scan, DefaultFleetCatalog.Aircraft2024);

        var types = await db.AircraftTypes.ToListAsync();
        var installs = await db.InstalledPackages.ToListAsync();
        var onDisk = installs.Where(i => i.IsOnDisk).Select(i => i.AircraftTypeId).ToHashSet();

        var dump = string.Join("\n", types
            .OrderByDescending(t => onDisk.Contains(t.Id))
            .ThenBy(t => t.CanonicalName)
            .Select(t =>
            {
                var src = onDisk.Contains(t.Id) ? "on-disk" : "default";
                var specs = t.CruiseKtas is int c
                    ? $"{t.Seats}s {t.UsefulLoadLbs}lb {c}kt rw{t.MinRunwayFt}"
                    : "(no specs)";
                return $"{src}  {t.Key,-12} {t.Category,-12} {specs,-26} {t.CanonicalName}";
            }));
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "callsign-roster-merged.txt"),
            $"IPP: {ipp}\nScanned: {scan.Count}   Total roster: {types.Count}\n\n{dump}\n");

        Assert.NotEmpty(types);
        Assert.Contains(types, t => t.Key == "C172"); // curated default fleet is present

        // If the community PC-12 is installed, it should be enriched with the curated specs.
        var pc12 = types.FirstOrDefault(t => t.Key == "PC12");
        if (pc12 is not null && onDisk.Contains(pc12.Id))
            Assert.True(pc12.CruiseKtas > 0);
    }
}
