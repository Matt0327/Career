using System.Text;

namespace Callsign.SimConnect;

/// <summary>
/// Helpers for reconciling the livery-dependent TITLE string the sim reports against
/// a stable aircraft type. Per the brief §5.3, the single most common failure mode
/// in this genre is title/type mismatch (liveries change the reported string). We
/// normalise aggressively and match on substring, and callers must never fail a
/// flight over a mismatch — warn and continue instead.
/// </summary>
public static class AircraftIdentity
{
    /// <summary>
    /// Normalise a raw title for comparison: lower-case, keep letters/digits, turn
    /// spaces/hyphens/underscores into single spaces, drop other punctuation.
    /// </summary>
    public static string Normalize(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return string.Empty;

        var sb = new StringBuilder(rawTitle.Length);
        foreach (var ch in rawTitle.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
                sb.Append(' ');
            // all other punctuation is dropped
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// True when <paramref name="observedTitle"/> plausibly refers to the same
    /// aircraft as any of <paramref name="typeAliases"/>. Substring match on the
    /// normalised forms, in either direction.
    /// </summary>
    public static bool Matches(string observedTitle, IEnumerable<string> typeAliases)
    {
        var observed = Normalize(observedTitle);
        if (observed.Length == 0)
            return false;

        foreach (var alias in typeAliases)
        {
            var a = Normalize(alias);
            if (a.Length == 0)
                continue;
            if (observed.Contains(a, StringComparison.Ordinal) ||
                a.Contains(observed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
