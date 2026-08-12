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
