using System.Text;

namespace Callsign.Core.Text;

/// <summary>
/// A pragmatic profanity gate for the free-text names a player picks — their callsign, airline, routes, and the
/// online display name that other people actually see (Phase 12). It is deliberately conservative: it catches the
/// clearly offensive cases the way a real game's name filter does, while avoiding the classic "Scunthorpe" false
/// positives (an innocent word that merely CONTAINS a rude fragment). Two tiers:
///
///  • <see cref="HardTerms"/> are matched as a substring of the leet-normalised name. These are slurs and hard
///    explicit words that essentially never occur inside an innocent English word, so a substring match is safe.
///  • <see cref="WordTerms"/> are the ambiguous ones — real fragments of ordinary words (…cl<b>ass</b>, ra<b>coon</b>,
///    S<b>cunt</b>horpe) — so they are matched ONLY as a whole token, never as a substring.
///
/// It is not a perfect classifier (no word filter is); it is a reasonable floor that keeps the obvious material out
/// of anything shown to other players. Pure and deterministic, so it's unit-tested without any I/O.
/// </summary>
public static class NameGuard
{
    // Slurs + hard explicit terms — safe to match anywhere in the (normalised) string.
    private static readonly string[] HardTerms =
    [
        "nigger", "nigga", "niglet", "faggot", "kike", "gook", "wetback", "beaner", "tranny",
        "chinaman", "coonass", "fuck", "shit", "phuck", "motherfucker", "cocksucker", "dickhead", "asshole",
    ];

    // Ambiguous terms — real fragments of ordinary words — matched ONLY as a whole token.
    private static readonly string[] WordTerms =
    [
        "cunt", "spic", "coon", "chink", "fag", "dyke", "ass", "dick", "cock", "cum", "piss", "twat",
        "wank", "slut", "whore", "bitch", "bastard", "pussy", "retard", "nazi", "rape", "jizz", "homo", "spaz",
    ];

    /// <summary>True when the name is clean (or blank — emptiness is a different validation's job).</summary>
    public static bool IsAllowed(string? name) => Match(name) is null;

    /// <summary>
    /// Throw a friendly <see cref="InvalidOperationException"/> if the name contains disallowed language. The
    /// route / airline / cloud endpoints already translate that into a clean 400, so callers just let it bubble.
    /// </summary>
    public static void Validate(string? name, string field = "name")
    {
        if (Match(name) is not null)
            throw new InvalidOperationException(
                $"That {field} contains language we can't allow — please choose something else.");
    }

    /// <summary>The offending term if the name is disallowed, otherwise null. (Internal — for tests/diagnostics.)</summary>
    internal static string? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string full = NormalizeLetters(name);
        if (full.Length == 0)
            return null;

        foreach (var term in HardTerms)
            if (full.Contains(term, StringComparison.Ordinal))
                return term;

        // Whole-token pass: normalise each word separately (and the whole string, so "a.s.s" → "ass" is caught),
        // and only flag an exact match — so "class", "raccoon", "Scunthorpe" sail through.
        foreach (var token in Tokenize(name))
            if (Array.IndexOf(WordTerms, token) >= 0)
                return token;
        if (Array.IndexOf(WordTerms, full) >= 0)
            return full;

        return null;
    }

    // Lowercase, fold the common leetspeak substitutions to letters, and drop everything that isn't a letter —
    // so "N1__GG3R", "f@ggot" and "n.i.g.g.e.r" all collapse to their plain form. Repeats are intentionally NOT
    // collapsed: folding "niiigger" would also fold "Nigeria"/"Niger" and mis-flag them, which we won't do.
    private static string NormalizeLetters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char raw in s)
        {
            char c = char.ToLowerInvariant(raw);
            c = c switch
            {
                '0' => 'o', '1' => 'i', '3' => 'e', '4' => 'a', '5' => 's',
                '6' => 'g', '7' => 't', '8' => 'b', '9' => 'g', '@' => 'a', '$' => 's', '|' => 'i',
                _ => c,
            };
            if (c >= 'a' && c <= 'z')
                sb.Append(c);
        }
        return sb.ToString();
    }

    // Split on any run of non-letter/non-digit characters, then normalise each word to its plain-letter form.
    private static IEnumerable<string> Tokenize(string s)
    {
        var word = new StringBuilder();
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                word.Append(c);
            else if (word.Length > 0)
            {
                yield return NormalizeLetters(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0)
            yield return NormalizeLetters(word.ToString());
    }
}
