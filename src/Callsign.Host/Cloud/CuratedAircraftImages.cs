namespace Callsign.Host.Cloud;

/// <summary>
/// A local, curated image override for the aircraft market — keyed by ICAO type designator (== AircraftType.Key
/// for curated types). Served BEFORE the cloud index so these hand-picked photos always win. The Host fetches
/// the URL once and caches the bytes (<see cref="AircraftImageCache"/>); Wikimedia is fetched with a descriptive
/// User-Agent (it 403s requests without one). Attribution is credited to Wikimedia Commons on the image caption.
/// </summary>
public static class CuratedAircraftImages
{
    public sealed record Entry(string Url, string Attribution, string? License, string SourceUrl);

    private static Entry Wm(string url) => new(url, "Wikimedia Commons", "CC BY-SA", url);

    public static readonly IReadOnlyDictionary<string, Entry> ByIcao = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
    {
        ["C152"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/9/94/Cessna_152_Aircraft.jpg/960px-Cessna_152_Aircraft.jpg"),
        ["C172"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/0/06/Cessna_172_Skyhawk_%28D-EDDX%29.jpg/960px-Cessna_172_Skyhawk_%28D-EDDX%29.jpg"),
        ["P28A"] = Wm("https://upload.wikimedia.org/wikipedia/commons/a/af/Piper_PA-28_Cherokee_A%C3%A9rodrome_de_Pontarlier_0008.jpg"),
        ["DR40"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/1/13/Robin_DR400-140B_Dauphin.jpg/960px-Robin_DR400-140B_Dauphin.jpg"),
        ["VL3"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/6/6b/JMB_Aircraft_%28Aveko%29_VL3_Evolution_%28D-MVLS%29_%282%29.jpg/960px-JMB_Aircraft_%28Aveko%29_VL3_Evolution_%28D-MVLS%29_%282%29.jpg"),
        ["SR20"] = Wm("https://upload.wikimedia.org/wikipedia/commons/b/b9/Cirrus_SR-20_AN1791727.jpg"),
        ["M20P"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/b/bf/Mooney.m20j.g-muni.arp.jpg/960px-Mooney.m20j.g-muni.arp.jpg"),
        ["SR22"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/8/80/Aeroporto_Passo_Fundo_%2814%29.jpg/1920px-Aeroporto_Passo_Fundo_%2814%29.jpg"),
        ["BN2A"] = Wm("https://upload.wikimedia.org/wikipedia/commons/a/a4/2023_Royal_International_Air_Tattoo_G-HYUK_%2853079771190%29.jpg"),
        ["BE58"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/c/c7/N600PE_Beechcraft_Baron_G58_%2830090126794%29.jpg/960px-N600PE_Beechcraft_Baron_G58_%2830090126794%29.jpg"),
        ["B06"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/7/78/LAPD_Bell_206_Jetranger.jpg/960px-LAPD_Bell_206_Jetranger.jpg"),
        ["BE60"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/4/40/Beechcraft_Duke_%2852464005282%29.jpg/960px-Beechcraft_Duke_%2852464005282%29.jpg"),
        ["SF50"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/4/44/N651AD_Vision_Jet%2C_Marana_Regional_03-01-26_%2855160647606%29.jpg/1920px-N651AD_Vision_Jet%2C_Marana_Regional_03-01-26_%2855160647606%29.jpg"),
        ["PC12"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/6/65/Pilatus_PC-12_OH-WAU_at_Portim%C3%A3o_Airport_12-06-2023_%282%29.jpg/960px-Pilatus_PC-12_OH-WAU_at_Portim%C3%A3o_Airport_12-06-2023_%282%29.jpg"),
        ["TBM9"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/0/02/M-ATTI_Socata_Daher_TBM_930_%2831117260275%29.jpg/960px-M-ATTI_Socata_Daher_TBM_930_%2831117260275%29.jpg"),
        ["AT76"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/2/2b/Flight_IXW_ATR_Indigo_Chennai_Aug22_D72_24849.jpg/960px-Flight_IXW_ATR_Indigo_Chennai_Aug22_D72_24849.jpg"),
        ["DH8D"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/e/e8/Air_Canada_Jazz_De_Havilland_Canada_DHC-8-102_Dash_8_C-GANQ_833_%287097888841%29.jpg/960px-Air_Canada_Jazz_De_Havilland_Canada_DHC-8-102_Dash_8_C-GANQ_833_%287097888841%29.jpg"),
        ["E55P"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/1/15/Embraer_Phenom_300_at_Christmas_Island_Airport.jpg/1920px-Embraer_Phenom_300_at_Christmas_Island_Airport.jpg"),
        ["LJ45"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/d/de/Learjet_45_%28561112136%29.jpg/960px-Learjet_45_%28561112136%29.jpg"),
        ["GLF6"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/d/d6/M-JCBB_Gulfstream_G650_JCB_Ltd_%2820282864898%29.jpg/960px-M-JCBB_Gulfstream_G650_JCB_Ltd_%2820282864898%29.jpg"),
        ["CRJ7"] = Wm("https://upload.wikimedia.org/wikipedia/commons/thumb/5/5b/American_Eagle_-_Bombardier_CRJ-702ER_-_N530EA_%28Quintin_Soloviev%29.jpg/960px-American_Eagle_-_Bombardier_CRJ-702ER_-_N530EA_%28Quintin_Soloviev%29.jpg"),
    };

    private static readonly HttpClient _http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Callsign/1.0 (MSFS career companion; contact via app)");
        return c;
    }

    public static bool TryGet(string key, out Entry entry) => ByIcao.TryGetValue(key.Trim(), out entry!);

    // Where the bundled photos live at runtime (copied next to the app; see Callsign.Host.csproj).
    private static readonly string LocalDir = Path.Combine(AppContext.BaseDirectory, "CuratedImages");

    /// <summary>
    /// The bundled curated photo bytes for a key, read from the app's own files (Phase 13) — no network, so the
    /// hand-picked images ALWAYS load. Wikimedia rate-limited runtime fetches, which made images flicker in and
    /// out; the photos are now shipped with the app (still credited to Wikimedia Commons on the caption).
    /// </summary>
    public static byte[]? LocalBytes(string key)
    {
        try
        {
            var path = LocalPath(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    private static string LocalPath(string key) => Path.Combine(LocalDir, key.Trim().ToUpperInvariant() + ".jpg");

    /// <summary>True if a photo for this key is bundled with the app.</summary>
    public static bool HasLocal(string key)
    {
        try { return File.Exists(LocalPath(key)); } catch { return false; }
    }

    /// <summary>Fetch the curated image bytes from the source URL (a last-resort fallback if the bundled file is
    /// missing); null on any failure so the caller falls back to the cloud.</summary>
    public static async Task<byte[]?> FetchAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return bytes.Length > 0 ? bytes : null;
        }
        catch { return null; }
    }
}
