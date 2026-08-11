using Callsign.Core.Aircraft;
using Xunit;

namespace Callsign.Core.Tests;

// Runs the scanner against the REAL MSFS install on this machine when present; on CI (no sim) it
// no-ops. It also dumps the roster to %TEMP%/callsign-roster.txt for manual inspection during dev.
public class InstalledAircraftScanRealTests
{
    [Fact]
    public void RealInstall_WhenPresent_ProducesAWellFormedRoster()
    {
        if (!MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp))
            return; // no MSFS here — nothing to verify

        var roster = new AircraftScanner().Scan(ipp);

        var dump = string.Join("\n", roster.Select(t =>
            $"{t.Key,-14} {t.Category,-12} {t.Titles.Count,2}x  {t.CanonicalName}  " +
            $"[{string.Join(",", t.Locations.Select(l => l.Source).Distinct())}]"));
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "callsign-roster.txt"),
            $"InstalledPackagesPath: {ipp}\nTypes: {roster.Count}\n\n{dump}\n");

        // Portable invariants only (specific aircraft vary by machine).
        Assert.All(roster, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Key));
            Assert.False(string.IsNullOrWhiteSpace(t.CanonicalName));
            Assert.NotEmpty(t.Titles);
            Assert.NotEmpty(t.Locations);
        });
    }
}
