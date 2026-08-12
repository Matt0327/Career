using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Callsign.Core.Data;

namespace Callsign.Host.Cloud;

// Wire shapes shared with Callsign.Server.
public record CloudProfile(string Id, string Email, string DisplayName, string? CreatedAt);
public record CloudAuth(string Token, CloudProfile User);
public record CloudSaveMeta(bool Exists, long SizeBytes, string? Device, string? UpdatedAt);
public record CloudCredsDto(string? Email, string? DisplayName, string? Password);
public record LeaderboardSubmit(long NetWorthCents, int Flights, int ReputationMilli, long Xp, string? RankKey);
public record LeaderboardRow(int Position, string DisplayName, long Value, string? RankKey, bool IsYou);
public record MyStanding(int? NetWorth, int? Flights, int? Reputation, int? Xp);

/// <summary>
/// The signed-in cloud session, persisted to a small JSON file next to the save so it survives restarts.
/// (A desktop-local token file, comparable to a browser's stored session — not a public surface.)
/// </summary>
public sealed class CloudSession
{
    private readonly string _path;
    private readonly object _gate = new();

    public string? Token { get; private set; }
    public CloudProfile? Profile { get; private set; }
    public bool IsSignedIn => !string.IsNullOrEmpty(Token);

    public CloudSession(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is not null && !string.IsNullOrEmpty(stored.Token))
            {
                Token = stored.Token;
                Profile = stored.Profile;
            }
        }
        catch { /* a corrupt session file just means "signed out" */ }
    }

    public void Set(string token, CloudProfile profile)
    {
        lock (_gate)
        {
            Token = token;
            Profile = profile;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(token, profile)));
            }
            catch { /* best-effort persistence; the in-memory session still works this run */ }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Token = null;
            Profile = null;
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* nothing to do */ }
        }
    }

    private record Stored(string Token, CloudProfile Profile);
}

/// <summary>
/// The app's gateway to Callsign Cloud. The desktop UI talks only to the local Host; the Host holds the
/// session token and makes the outbound calls — so there is no CORS, the token never sits in the browser,
/// and save transfers go server-to-server where large files stream cleanly.
/// </summary>
public sealed class CloudGateway
{
    private readonly HttpClient _http;
    private readonly SaveService _saves;

    public CloudGateway(HttpClient http, CloudSession session, SaveService saves)
    {
        _http = http;
        Session = session;
        _saves = saves;
    }

    public CloudSession Session { get; }
    public string BaseUrl => _http.BaseAddress?.ToString() ?? "";

    public sealed record Result(bool Ok, string? Error = null);

    public Task<Result> RegisterAsync(string email, string displayName, string password) =>
        AuthAsync("/api/auth/register", new { email, displayName, password });

    public Task<Result> LoginAsync(string email, string password) =>
        AuthAsync("/api/auth/login", new { email, password });

    private async Task<Result> AuthAsync(string path, object body)
    {
        HttpResponseMessage resp;
        try { resp = await _http.PostAsJsonAsync(path, body); }
        catch (Exception ex) { return new Result(false, "Can't reach Callsign Cloud — " + ex.Message); }
        if (!resp.IsSuccessStatusCode) return new Result(false, await ErrorTextAsync(resp));
        var auth = await resp.Content.ReadFromJsonAsync<CloudAuth>();
        if (auth is null) return new Result(false, "Unexpected response from Cloud.");
        Session.Set(auth.Token, auth.User);
        return new Result(true);
    }

    public async Task LogoutAsync()
    {
        try { if (Session.IsSignedIn) await _http.SendAsync(Authed(HttpMethod.Post, "/api/auth/logout")); }
        catch { /* signing out locally is what matters */ }
        Session.Clear();
    }

    public async Task<CloudSaveMeta?> SaveMetaAsync()
    {
        if (!Session.IsSignedIn) return null;
        try
        {
            var resp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/save/meta"));
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<CloudSaveMeta>() : null;
        }
        catch { return null; }
    }

    /// <summary>Snapshot the live save and upload it as the user's latest cloud save.</summary>
    public async Task<Result> PushAsync(CallsignDbContext db)
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        var temp = Path.Combine(Path.GetTempPath(), "callsign-push-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await _saves.SnapshotToAsync(db, temp);
            byte[] data = await File.ReadAllBytesAsync(temp);
            var req = Authed(HttpMethod.Put, "/api/save");
            req.Content = new ByteArrayContent(data);
            req.Content.Headers.ContentType = new("application/octet-stream");
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? new Result(true) : new Result(false, await ErrorTextAsync(resp));
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best-effort */ } }
    }

    /// <summary>Download the cloud save and stage it to replace the local save on the next launch.</summary>
    public async Task<Result> PullAsync()
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        try
        {
            var resp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/save"));
            if (resp.StatusCode == HttpStatusCode.NotFound) return new Result(false, "There is no cloud save yet.");
            if (!resp.IsSuccessStatusCode) return new Result(false, await ErrorTextAsync(resp));
            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            _saves.StageRestoreFromBytes(data);
            return new Result(true);
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
    }

    /// <summary>Fetch the approved index image for an aircraft key (public read, no auth). Null if none.</summary>
    public async Task<(byte[] Data, string ContentType)?> GetTypeImageAsync(string key)
    {
        try
        {
            var resp = await _http.GetAsync("/api/images/" + Uri.EscapeDataString(key));
            if (!resp.IsSuccessStatusCode) return null;
            var data = await resp.Content.ReadAsByteArrayAsync();
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            return data.Length == 0 ? null : (data, contentType);
        }
        catch { return null; }
    }

    public async Task<Result> SubmitLeaderboardAsync(LeaderboardSubmit stats)
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        try
        {
            var req = Authed(HttpMethod.Post, "/api/leaderboard");
            req.Content = JsonContent.Create(stats);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? new Result(true) : new Result(false, await ErrorTextAsync(resp));
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
    }

    public async Task<List<LeaderboardRow>> GetLeaderboardAsync(string board, int limit)
    {
        try
        {
            // Send the token when we have one, so the caller's own row comes back flagged as "you".
            var req = Authed(HttpMethod.Get, $"/api/leaderboard?board={Uri.EscapeDataString(board)}&limit={limit}");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new List<LeaderboardRow>();
            return await resp.Content.ReadFromJsonAsync<List<LeaderboardRow>>() ?? new List<LeaderboardRow>();
        }
        catch { return new List<LeaderboardRow>(); }
    }

    public async Task<MyStanding?> GetMyStandingAsync()
    {
        if (!Session.IsSignedIn) return null;
        try
        {
            var resp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/leaderboard/me"));
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<MyStanding>() : null;
        }
        catch { return null; }
    }

    private HttpRequestMessage Authed(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        if (Session.Token is { } token) req.Headers.Add("Authorization", "Bearer " + token);
        return req;
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage resp)
    {
        try
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (doc.ValueKind == JsonValueKind.Object &&
                doc.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString()!;
        }
        catch { /* fall through to a generic message */ }
        return "Cloud request failed (" + (int)resp.StatusCode + ").";
    }
}

/// <summary>A tiny on-disk cache of index images by aircraft key, so the app doesn't refetch on every render.</summary>
public sealed class AircraftImageCache
{
    private readonly string _dir;
    public AircraftImageCache(string dir) => _dir = dir;

    private string PathFor(string key) => Path.Combine(_dir, Safe(key));
    private static string Safe(string key) => string.Concat(key.Trim().ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public byte[]? TryGet(string key)
    {
        try { var p = PathFor(key); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    public void Put(string key, byte[] data)
    {
        try { Directory.CreateDirectory(_dir); File.WriteAllBytes(PathFor(key), data); }
        catch { /* a cache miss next time is harmless */ }
    }
}

/// <summary>Detect an image's content type from its magic bytes (default image/jpeg).</summary>
public static class ImageSniff
{
    public static string ContentType(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length >= 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 &&
            d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
        return "image/jpeg";
    }
}
