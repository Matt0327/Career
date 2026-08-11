namespace Callsign.Core.Aircraft;

/// <summary>
/// A parsed aircraft.cfg: the <c>[GENERAL]</c> section plus each <c>[FLTSIM.N]</c> livery block.
/// Tolerant of the format's quirks — space around <c>=</c>, quoted values, capitalised keys, and
/// <c>;</c> or <c>//</c> comment lines.
/// </summary>
public sealed class AircraftCfg
{
    public IReadOnlyDictionary<string, string> General { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, string>> FltSims { get; }

    private AircraftCfg(Dictionary<string, string> general, List<IReadOnlyDictionary<string, string>> fltSims)
    {
        General = general;
        FltSims = fltSims;
    }

    public static AircraftCfg Parse(TextReader reader)
    {
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fltSims = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith(';') || t.StartsWith("//"))
                continue;

            if (t.StartsWith('['))
            {
                int end = t.IndexOf(']');
                var section = (end > 0 ? t[1..end] : t.Trim('[', ']')).Trim();
                if (section.StartsWith("FLTSIM", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    fltSims.Add(current);
                }
                else if (section.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
                {
                    current = general;
                }
                else
                {
                    current = null; // sections we don't care about
                }
                continue;
            }

            if (current is null)
                continue;

            int eq = t.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = t[..eq].Trim();
            var value = CleanValue(t[(eq + 1)..]);
            if (key.Length > 0)
                current[key] = value;
        }

        return new AircraftCfg(general, fltSims);
    }

    // Extract a value, honouring surrounding quotes and stripping inline ';' or '//' comments
    // (e.g. Title="SR-71 ASARS" ; Variation name  ->  SR-71 ASARS).
    private static string CleanValue(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            int close = s.IndexOf('"', 1);
            return close > 0 ? s[1..close] : s[1..];
        }

        int cut = -1;
        int semi = s.IndexOf(';');
        if (semi >= 0) cut = semi;
        int dbl = s.IndexOf("//", StringComparison.Ordinal);
        if (dbl >= 0 && (cut < 0 || dbl < cut)) cut = dbl;
        if (cut >= 0) s = s[..cut];
        return s.Trim();
    }
}
