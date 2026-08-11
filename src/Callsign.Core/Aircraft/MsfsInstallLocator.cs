using System.Text.RegularExpressions;

namespace Callsign.Core.Aircraft;

/// <summary>
/// Finds the sim's package folder without ever asking the user to place or point at a file
/// (brief §3.6): it reads <c>InstalledPackagesPath</c> out of MSFS's own <c>UserCfg.opt</c>,
/// trying the Store (Microsoft.Limitless) and Steam locations.
/// </summary>
public static partial class MsfsInstallLocator
{
    public static bool TryGetInstalledPackagesPath(out string path, string? userCfgOverride = null)
    {
        path = "";
        var cfg = userCfgOverride ?? FindUserCfg();
        if (cfg is null || !File.Exists(cfg))
            return false;

        foreach (var line in File.ReadLines(cfg))
        {
            var parsed = ParseInstalledPackagesPath(line);
            if (parsed is not null)
            {
                path = parsed;
                return true;
            }
        }
        return false;
    }

    /// <summary>Pull the quoted path out of an <c>InstalledPackagesPath "..."</c> line, or null.</summary>
    internal static string? ParseInstalledPackagesPath(string line)
    {
        var m = PathLine().Match(line);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? FindUserCfg()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex("InstalledPackagesPath\\s+\"?([^\"]+)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex PathLine();
}
