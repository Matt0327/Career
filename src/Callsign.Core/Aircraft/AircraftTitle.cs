using System.Text;

namespace Callsign.Core.Aircraft;

/// <summary>
/// Normalises a livery TITLE string for comparison — the crux of reconciling the sim's reported
/// title against a stable type (brief §5.3). Lower-cases, keeps letters/digits, turns
/// spaces/hyphens/underscores into single spaces, drops other punctuation.
/// </summary>
public static class AircraftTitle
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
                sb.Append(' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
