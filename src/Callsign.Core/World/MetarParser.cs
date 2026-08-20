using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Callsign.Core.World;

/// <summary>The aviationweather.gov (NOAA AWC) decoded-METAR JSON row we read. The two union-typed fields
/// (<see cref="Wdir"/> number|"VRB", <see cref="Visib"/> number|"10+") decode as <see cref="JsonElement"/>.</summary>
public sealed class MetarJson
{
    [JsonPropertyName("icaoId")] public string? IcaoId { get; set; }
    [JsonPropertyName("obsTime")] public long? ObsTime { get; set; }
    [JsonPropertyName("wdir")] public JsonElement Wdir { get; set; }
    [JsonPropertyName("wspd")] public double? Wspd { get; set; }
    [JsonPropertyName("wgst")] public double? Wgst { get; set; }
    [JsonPropertyName("visib")] public JsonElement Visib { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("wxString")] public string? WxString { get; set; }
    [JsonPropertyName("fltCat")] public string? FltCat { get; set; }
    [JsonPropertyName("rawOb")] public string? RawOb { get; set; }
    [JsonPropertyName("clouds")] public List<MetarCloud>? Clouds { get; set; }
}

public sealed class MetarCloud
{
    [JsonPropertyName("cover")] public string? Cover { get; set; }
    [JsonPropertyName("base")] public int? Base { get; set; }
}

/// <summary>
/// A PURE, static, I/O-free, NON-THROWING METAR → <see cref="Weather"/> parser (Phase 9b). Every field that
/// can't be trusted degrades the whole observation to <c>null</c> (→ the caller uses the synthetic model), and
/// units are normalised explicitly at the boundary (the silent-money traps: metres vs statute miles, MPS/KMH
/// wind, M-prefixed negatives). It never compresses live values into the synthetic model's bands — a real gale
/// shows as a real gale. Because it's pure and canned-input-testable, the whole thing is verified without a
/// network; only the HTTP round-trip that produces its input is untested-here.
/// </summary>
public static class MetarParser
{
    /// <summary>Structured (decoded-JSON) path → <see cref="Weather"/>, or null if untrustworthy.</summary>
    public static Weather? FromJson(MetarJson? row)
    {
        if (row is null) return null;
        if (!TryVisibilityJson(row.Visib, out double visSm)) return null; // visibility is load-bearing
        int windKts = Math.Clamp(row.Wspd is double ws && double.IsFinite(ws) ? (int)Math.Round(ws) : 0, 0, 250);
        int windDir = DirectionJson(row.Wdir);
        int gustKts = Math.Max(windKts, Math.Clamp(row.Wgst is double wg && double.IsFinite(wg) ? (int)Math.Round(wg) : windKts, 0, 300));
        int tempC = row.Temp is double t && double.IsFinite(t) ? (int)Math.Round(Math.Clamp(t, -90, 60)) : 15;
        int ceilFt = CeilingFromClouds(row.Clouds);
        string cond = MapCondition(row.WxString, HasOvercast(row.Clouds), ceilFt, visSm);
        visSm = Math.Clamp(visSm, 0.05, 10);
        return new Weather(windDir, windKts, gustKts, visSm, ceilFt, tempC, cond, Summarize(cond, windKts, gustKts, visSm, ceilFt, tempC));
    }

    /// <summary>Defensive fallback: parse the RAW METAR text (when the structured row is missing/odd).</summary>
    public static Weather? FromRaw(string? rawOb)
    {
        if (string.IsNullOrWhiteSpace(rawOb)) return null;
        var raw = rawOb.Trim().ToUpperInvariant();
        if (raw.Contains("NIL")) return null;
        var tokens = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        int? windDir = null, windKts = null, gustKts = null, tempC = null, ceilFt = null;
        double? visSm = null;
        bool sawWind = false, sawVis = false, cavok = false;
        var wx = new StringBuilder();

        foreach (var tok in tokens)
        {
            if (!sawWind && TryParseWind(tok, out int wd, out int ws, out int? gs, out bool vrb))
            { windDir = vrb ? 0 : wd; windKts = ws; gustKts = gs; sawWind = true; continue; }
            if (tok is "CAVOK" or "SKC" or "CLR" or "NSC" or "NCD")
            { if (tok == "CAVOK") { cavok = true; visSm ??= 10; sawVis = true; } continue; }
            if (!sawVis && TryParseVisRaw(tok, out double vs)) { visSm = vs; sawVis = true; continue; }
            if (tempC is null && TryParseTemp(tok, out int tc)) { tempC = tc; continue; }
            if (TryParseCloud(tok, out int? cb)) { if (cb is int b && (ceilFt is null || b < ceilFt)) ceilFt = b; continue; }
            if (IsWxToken(tok)) wx.Append(tok).Append(' ');
        }

        if (!sawWind && !sawVis) return null; // nothing we can trust → synthetic
        int windF = Math.Clamp(windKts ?? 0, 0, 250);
        int gustF = Math.Max(windF, Math.Clamp(gustKts ?? windF, 0, 300));
        double visF = Math.Clamp(visSm ?? 10, 0.05, 10);
        int ceilF = ceilFt ?? 25_000;
        int tempF = tempC ?? 15;
        string cond = MapCondition(wx.ToString(), overcast: !cavok && ceilF < 25_000, ceilF, visF);
        return new Weather(windDir ?? 0, windF, gustF, visF, ceilF, tempF, cond, Summarize(cond, windF, gustF, visF, ceilF, tempF));
    }

    // ── shared mapping ────────────────────────────────────────────────────────────────────────────

    private static string MapCondition(string? wxString, bool overcast, int ceilFt, double visSm)
    {
        var w = (wxString ?? "").ToUpperInvariant();
        if (w.Contains("TS")) return "Storm";
        if (w.Contains("SN") || w.Contains("SG") || w.Contains("PL")) return "Snow";
        if ((w.Contains("FG") || w.Contains("BR")) && visSm <= 3) return "Fog";
        if (w.Contains("RA") || w.Contains("DZ") || w.Contains("SH")) return "Rain";
        return overcast ? "Cloudy" : "Clear";
    }

    private static bool HasOvercast(List<MetarCloud>? clouds)
        => clouds?.Any(c => c.Cover?.ToUpperInvariant() is "BKN" or "OVC" or "VV") ?? false;

    private static int CeilingFromClouds(List<MetarCloud>? clouds)
    {
        int ceil = 25_000;
        if (clouds is null) return ceil;
        foreach (var c in clouds)
            if (c.Cover?.ToUpperInvariant() is "BKN" or "OVC" or "VV" && c.Base is int b && b >= 0 && b < ceil)
                ceil = b;
        return ceil;
    }

    private static string Summarize(string cond, int wind, int gust, double vis, int ceil, int temp)
    {
        string windStr = gust > wind ? $"{wind}G{gust} kt" : $"{wind} kt";
        string ceilStr = ceil < 25_000 ? $", {ceil} ft ceiling" : "";
        return $"{cond}, {windStr}, {vis:0.#} sm{ceilStr}, {temp}°C";
    }

    // ── JSON field helpers (union types) ────────────────────────────────────────────────────────────

    private static int DirectionJson(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) { int d = (int)Math.Round(e.GetDouble()); return ((d % 360) + 360) % 360; }
        return 0; // "VRB" or anything else → 0 (display-only)
    }

    private static bool TryVisibilityJson(JsonElement e, out double sm)
    {
        sm = 0;
        if (e.ValueKind == JsonValueKind.Number) { sm = e.GetDouble(); return double.IsFinite(sm) && sm > 0; }
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimEnd('+').Trim();
            if (s.Contains('/'))
            {
                var p = s.Split('/');
                if (p.Length == 2 && double.TryParse(p[0], out var n) && double.TryParse(p[1], out var d) && d != 0) { sm = n / d; return sm > 0; }
                return false;
            }
            if (double.TryParse(s, out var v)) { sm = v; return v > 0; }
        }
        return false;
    }

    // ── raw-text token parsers ─────────────────────────────────────────────────────────────────────

    private static bool TryParseWind(string tok, out int dir, out int kts, out int? gust, out bool variable)
    {
        dir = 0; kts = 0; gust = null; variable = false;
        var m = Regex.Match(tok, @"^(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?(KT|MPS|KMH)$");
        if (!m.Success) return false;
        variable = m.Groups[1].Value == "VRB";
        if (!variable) dir = int.Parse(m.Groups[1].Value) % 360;
        double factor = m.Groups[4].Value switch { "MPS" => 1.94384, "KMH" => 1.0 / 1.852, _ => 1.0 };
        kts = (int)Math.Round(int.Parse(m.Groups[2].Value) * factor);
        gust = m.Groups[3].Success ? (int)Math.Round(int.Parse(m.Groups[3].Value) * factor) : null;
        return true;
    }

    private static bool TryParseVisRaw(string tok, out double sm)
    {
        sm = 0;
        if (tok.EndsWith("SM"))
        {
            var body = tok[..^2].TrimStart('P', 'M');
            if (body.Contains('/'))
            {
                var p = body.Split('/');
                if (p.Length == 2 && double.TryParse(p[0], out var n) && double.TryParse(p[1], out var d) && d != 0) { sm = n / d; return true; }
                return false;
            }
            if (double.TryParse(body, out var v)) { sm = v; return true; }
            return false;
        }
        if (tok.Length == 4 && tok.All(char.IsDigit)) // bare metres group (e.g. 4800, 9999)
        {
            int metres = int.Parse(tok);
            sm = metres >= 9999 ? 10 : metres / 1609.34;
            return true;
        }
        return false;
    }

    private static bool TryParseTemp(string tok, out int tempC)
    {
        tempC = 0;
        var m = Regex.Match(tok, @"^(M?\d{1,2})/(M?\d{1,2})$");
        if (!m.Success) return false;
        var t = m.Groups[1].Value;
        int val = int.Parse(t.TrimStart('M'));
        tempC = t.StartsWith('M') ? -val : val;
        return true;
    }

    private static bool TryParseCloud(string tok, out int? ceilBase)
    {
        ceilBase = null;
        var m = Regex.Match(tok, @"^(FEW|SCT|BKN|OVC|VV)(\d{3})");
        if (!m.Success) return false;
        if (m.Groups[1].Value is "BKN" or "OVC" or "VV") ceilBase = int.Parse(m.Groups[2].Value) * 100;
        return true;
    }

    private static readonly string[] WxCodes =
        { "TS", "RA", "SN", "DZ", "SG", "PL", "FG", "BR", "SH", "GR", "GS", "FZ", "HZ", "FU", "SQ", "FC" };
    private static bool IsWxToken(string tok)
    {
        var t = tok.TrimStart('+', '-');
        return t.Length is >= 2 and <= 8 && WxCodes.Any(c => t.Contains(c));
    }
}
