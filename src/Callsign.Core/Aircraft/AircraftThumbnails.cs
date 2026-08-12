namespace Callsign.Core.Aircraft;

/// <summary>
/// Finds an aircraft's own <c>thumbnail.jpg</c> in the player's MSFS install (Phase 6b). The scan records
/// only folder <em>names</em> (<see cref="Callsign.Core.Domain.InstalledPackage"/>), so we rebuild the
/// package path from the install root and search it — bounded to that one package, robust to how deeply the
/// aircraft is nested — preferring the thumbnail inside the matching aircraft folder. The image is the
/// player's own installed content, read locally and served only to them; it is never bundled or shipped.
/// </summary>
public static class AircraftThumbnails
{
    public static string? TryResolve(string installedPackagesPath, string source, string packageFolder, string aircraftFolder)
    {
        if (string.IsNullOrWhiteSpace(installedPackagesPath) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(packageFolder))
            return null;
        try
        {
            var packageRoot = Path.Combine(installedPackagesPath, source, packageFolder);
            if (!Directory.Exists(packageRoot))
                return null;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            string? firstAny = null;
            var marker = Path.DirectorySeparatorChar + (aircraftFolder ?? "") + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(packageRoot, "thumbnail.jpg", options))
            {
                firstAny ??= file;
                // The thumbnail sitting inside this exact aircraft's folder is the right one (a package can
                // hold several aircraft, each with its own).
                if (!string.IsNullOrEmpty(aircraftFolder) &&
                    file.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return firstAny; // single-aircraft package, or an odd layout — the only thumbnail is the one
        }
        catch
        {
            return null; // never let a bad path break a card; the UI falls back to a silhouette
        }
    }
}
