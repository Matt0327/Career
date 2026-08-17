using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Callsign.Core.Data;

namespace Callsign.Host.Cloud;

// The desktop app's gateway to Callsign Cloud, which is Supabase (Auth + Storage + PostgREST). The WebView2
// UI still talks only to the local Host; the Host holds the Supabase session and makes the outbound calls.

public record SupabaseConfig(string Url, string AnonKey);

// Wire shapes the Host endpoints/UI use.
public record CloudSaveMeta(bool Exists, long SizeBytes, string? Device, string? UpdatedAt);
public record CloudCredsDto(string? Email, string? DisplayName, string? Password);
public record LeaderboardSubmit(long NetWorthCents, int Flights, int ReputationMilli, long Xp, string? RankKey);
public record LeaderboardRow(int Position, string DisplayName, long Value, string? RankKey, bool IsYou);

/// <summary>
/// The signed-in Supabase session (access + refresh tokens, user id, display name), persisted next to the
/// save so sign-in survives restarts. Only ever holds tokens — never the password.
/// </summary>
public sealed class CloudSession
{
    private readonly string _path;
    private readonly object _gate = new();

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public bool IsSignedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(UserId);

    public CloudSession(string path) { _path = path; Load(); }

    public void Set(string access, string? refresh, DateTimeOffset expires, string userId, string? email, string? displayName)
    {
        lock (_gate)
        {
            AccessToken = access; RefreshToken = refresh; ExpiresAt = expires;
            UserId = userId; Email = email; DisplayName = displayName;
            Persist();
        }
    }

    public void UpdateTokens(string access, string? refresh, DateTimeOffset expires)
    {
        lock (_gate)
        {
            AccessToken = access;
            if (!string.IsNullOrEmpty(refresh)) RefreshToken = refresh;
            ExpiresAt = expires;
            Persist();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            AccessToken = RefreshToken = UserId = Email = DisplayName = null;
            ExpiresAt = default;
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* nothing to do */ }
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(AccessToken, RefreshToken, ExpiresAt, UserId, Email, DisplayName)));
        }
        catch { /* best-effort; the in-memory session still works this run */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (s is not null && !string.IsNullOrEmpty(s.AccessToken))
            {
                AccessToken = s.AccessToken; RefreshToken = s.RefreshToken; ExpiresAt = s.ExpiresAt;
                UserId = s.UserId; Email = s.Email; DisplayName = s.DisplayName;
            }
        }
        catch { /* a corrupt session file just means "signed out" */ }
    }

    private record Stored(string? AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? UserId, string? Email, string? DisplayName);
}

public sealed class CloudGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, // C# NetWorthCents <-> db net_worth_cents
    };

    private readonly HttpClient _http;
    private readonly SaveService _saves;
    private readonly string _anonKey;

    public CloudGateway(HttpClient http, CloudSession session, SaveService saves, SupabaseConfig cfg)
    {
        _http = http;
        Session = session;
        _saves = saves;
        _anonKey = cfg.AnonKey;
    }

    public CloudSession Session { get; }
    public string BaseUrl => _http.BaseAddress?.ToString() ?? "";

    public sealed record Result(bool Ok, string? Error = null);

    // ── auth ───────────────────────────────────────────────────────────────────────────────────────
    public async Task<Result> RegisterAsync(string email, string displayName, string password)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(AuthRequest("/auth/v1/signup", new { email, password, data = new { display_name = displayName } })); }
        catch (Exception ex) { return new Result(false, "Can't reach Callsign Cloud — " + ex.Message); }
        if (!resp.IsSuccessStatusCode) return new Result(false, await AuthErrorAsync(resp));

        var s = await ReadAsync<SupabaseSession>(resp);
        if (s?.AccessToken is { Length: > 0 }) { StoreSession(s); return new Result(true); }
        return new Result(false, "Account created — confirm your email, then sign in. (You can turn email confirmation off in Supabase while testing.)");
    }

    public async Task<Result> LoginAsync(string email, string password)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(AuthRequest("/auth/v1/token?grant_type=password", new { email, password })); }
        catch (Exception ex) { return new Result(false, "Can't reach Callsign Cloud — " + ex.Message); }
        if (!resp.IsSuccessStatusCode) return new Result(false, await AuthErrorAsync(resp));

        var s = await ReadAsync<SupabaseSession>(resp);
        if (s?.AccessToken is { Length: > 0 }) { StoreSession(s); return new Result(true); }
        return new Result(false, "Sign-in failed.");
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (Session.IsSignedIn)
                await _http.SendAsync(Rest(HttpMethod.Post, "/auth/v1/logout", authed: true));
        }
        catch { /* signing out locally is what matters */ }
        Session.Clear();
    }

    // Refresh the access token when it is close to expiry; on failure, sign out.
    private async Task EnsureFreshTokenAsync()
    {
        if (!Session.IsSignedIn) return;
        if (Session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return;
        if (Session.RefreshToken is not { Length: > 0 } refresh) return;
        try
        {
            var resp = await _http.SendAsync(AuthRequest("/auth/v1/token?grant_type=refresh_token", new { refresh_token = refresh }));
            if (!resp.IsSuccessStatusCode) { Session.Clear(); return; }
            var s = await ReadAsync<SupabaseSession>(resp);
            if (s?.AccessToken is { Length: > 0 })
                Session.UpdateTokens(s.AccessToken, s.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(s.ExpiresIn > 0 ? s.ExpiresIn : 3600));
            else Session.Clear();
        }
        catch { /* keep the (possibly stale) token; the next call will surface the real error */ }
    }

    private void StoreSession(SupabaseSession s)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(s.ExpiresIn > 0 ? s.ExpiresIn : 3600);
        Session.Set(s.AccessToken!, s.RefreshToken, expires, s.User?.Id ?? "", s.User?.Email, DisplayNameOf(s.User) ?? "Pilot");
    }

    // ── cloud saves (Supabase Storage: saves/{uid}/save.db) ─────────────────────────────────────────
    public async Task<CloudSaveMeta?> SaveMetaAsync()
    {
        if (!Session.IsSignedIn) return null;
        await EnsureFreshTokenAsync();
        try
        {
            var resp = await _http.SendAsync(Rest(HttpMethod.Get, $"/rest/v1/cloud_saves?user_id=eq.{Session.UserId}&select=size_bytes,device,updated_at", authed: true));
            if (!resp.IsSuccessStatusCode) return null;
            var rows = await ReadAsync<List<CloudSaveRow>>(resp);
            var row = rows is { Count: > 0 } ? rows[0] : null;
            return row is null ? new CloudSaveMeta(false, 0, null, null)
                               : new CloudSaveMeta(true, row.SizeBytes, row.Device, row.UpdatedAt);
        }
        catch { return null; }
    }

    public async Task<Result> PushAsync(CallsignDbContext db)
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        await EnsureFreshTokenAsync();
        var temp = Path.Combine(Path.GetTempPath(), "callsign-push-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await _saves.SnapshotToAsync(db, temp);
            byte[] data = await File.ReadAllBytesAsync(temp);

            var upload = Rest(HttpMethod.Post, $"/storage/v1/object/saves/{Session.UserId}/save.db", authed: true);
            upload.Headers.TryAddWithoutValidation("x-upsert", "true");
            upload.Content = new ByteArrayContent(data);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var up = await _http.SendAsync(upload);
            if (!up.IsSuccessStatusCode) return new Result(false, await ErrorAsync(up));

            var meta = Rest(HttpMethod.Post, "/rest/v1/cloud_saves", authed: true);
            meta.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates");
            meta.Content = JsonContent.Create(new
            {
                user_id = Session.UserId,
                size_bytes = data.LongLength,
                device = "Callsign Desktop",
                updated_at = DateTimeOffset.UtcNow,
            });
            await _http.SendAsync(meta); // metadata is best-effort; the file upload is the source of truth
            return new Result(true);
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp cleanup is best-effort */ } }
    }

    public async Task<Result> PullAsync()
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        await EnsureFreshTokenAsync();
        try
        {
            var resp = await _http.SendAsync(Rest(HttpMethod.Get, $"/storage/v1/object/authenticated/saves/{Session.UserId}/save.db", authed: true));
            if (resp.StatusCode == HttpStatusCode.NotFound) return new Result(false, "There is no cloud save yet.");
            if (!resp.IsSuccessStatusCode) return new Result(false, await ErrorAsync(resp));
            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            _saves.StageRestoreFromBytes(data);
            return new Result(true);
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
    }

    // ── aircraft image index (public reads) ─────────────────────────────────────────────────────────
    public async Task<(byte[] Data, string ContentType)?> GetTypeImageAsync(string key)
    {
        try
        {
            var k = Uri.EscapeDataString(key.Trim().ToUpperInvariant());
            var lookup = await _http.SendAsync(Rest(HttpMethod.Get,
                $"/rest/v1/aircraft_images?key=eq.{k}&status=eq.approved&select=storage_path,content_type&order=sort_rank.desc&limit=1", authed: false));
            if (!lookup.IsSuccessStatusCode) return null;
            var rows = await ReadAsync<List<ImageRow>>(lookup);
            if (rows is not { Count: > 0 } || string.IsNullOrEmpty(rows[0].StoragePath)) return null;

            var file = await _http.SendAsync(Rest(HttpMethod.Get,
                "/storage/v1/object/public/aircraft-images/" + rows[0].StoragePath, authed: false));
            if (!file.IsSuccessStatusCode) return null;
            var data = await file.Content.ReadAsByteArrayAsync();
            var contentType = file.Content.Headers.ContentType?.ToString() ?? rows[0].ContentType ?? "image/jpeg";
            return data.Length == 0 ? null : (data, contentType);
        }
        catch { return null; }
    }

    /// <summary>The credit (attribution + license) for the approved index image of a key, for the caption.</summary>
    public async Task<(string? Attribution, string? License, string? SourceUrl)?> GetTypeImageMetaAsync(string key)
    {
        try
        {
            var k = Uri.EscapeDataString(key.Trim().ToUpperInvariant());
            var resp = await _http.SendAsync(Rest(HttpMethod.Get,
                $"/rest/v1/aircraft_images?key=eq.{k}&status=eq.approved&select=attribution,license,source_url&order=sort_rank.desc&limit=1", authed: false));
            if (!resp.IsSuccessStatusCode) return null;
            var rows = await ReadAsync<List<ImageMetaRow>>(resp);
            if (rows is not { Count: > 0 }) return null;
            return (rows[0].Attribution, rows[0].License, rows[0].SourceUrl);
        }
        catch { return null; }
    }

    // ── leaderboards ────────────────────────────────────────────────────────────────────────────────
    public async Task<Result> SubmitLeaderboardAsync(LeaderboardSubmit stats)
    {
        if (!Session.IsSignedIn) return new Result(false, "Sign in first.");
        await EnsureFreshTokenAsync();
        try
        {
            var req = Rest(HttpMethod.Post, "/rest/v1/leaderboard_stats", authed: true);
            req.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates");
            req.Content = JsonContent.Create(new
            {
                user_id = Session.UserId,
                net_worth_cents = stats.NetWorthCents,
                flights = stats.Flights,
                reputation_milli = stats.ReputationMilli,
                xp = stats.Xp,
                rank_key = stats.RankKey,
                updated_at = DateTimeOffset.UtcNow,
            });
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode ? new Result(true) : new Result(false, await ErrorAsync(resp));
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
    }

    public async Task<List<LeaderboardRow>> GetLeaderboardAsync(string board, int limit)
    {
        try
        {
            var req = Rest(HttpMethod.Post, "/rest/v1/rpc/leaderboard", authed: Session.IsSignedIn);
            req.Content = JsonContent.Create(new { board, lim = limit });
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new List<LeaderboardRow>();
            var rows = await ReadAsync<List<RpcRow>>(resp) ?? new List<RpcRow>();
            return rows.Select(r => new LeaderboardRow(
                (int)r.Position, r.DisplayName ?? "Pilot", r.Value, r.RankKey,
                !string.IsNullOrEmpty(Session.UserId) && r.UserId == Session.UserId)).ToList();
        }
        catch { return new List<LeaderboardRow>(); }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────
    private HttpRequestMessage Rest(HttpMethod method, string path, bool authed)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("apikey", _anonKey);
        var bearer = authed && Session.AccessToken is { Length: > 0 } ? Session.AccessToken : _anonKey;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private HttpRequestMessage AuthRequest(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.TryAddWithoutValidation("apikey", _anonKey);
        req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage resp)
        => await resp.Content.ReadFromJsonAsync<T>(Json);

    private static string? DisplayNameOf(SupabaseUser? user)
        => user?.UserMetadata is { } m && m.TryGetValue("display_name", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // Supabase auth errors come back as { error_description } / { msg } / { message }.
    private static async Task<string> AuthErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var field in new[] { "error_description", "msg", "message", "error" })
                if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString()!;
        }
        catch { /* fall through */ }
        return resp.StatusCode == HttpStatusCode.BadRequest ? "Wrong email or password." : $"Cloud request failed ({(int)resp.StatusCode}).";
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var field in new[] { "message", "error", "msg" })
                if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString()!;
        }
        catch { /* fall through */ }
        return $"Cloud request failed ({(int)resp.StatusCode}).";
    }

    // read DTOs
    private sealed class SupabaseSession
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public SupabaseUser? User { get; set; }
    }
    private sealed class SupabaseUser
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public Dictionary<string, JsonElement>? UserMetadata { get; set; }
    }
    private sealed class CloudSaveRow
    {
        public long SizeBytes { get; set; }
        public string? Device { get; set; }
        public string? UpdatedAt { get; set; }
    }
    private sealed class ImageRow
    {
        public string? StoragePath { get; set; }
        public string? ContentType { get; set; }
    }
    private sealed class ImageMetaRow
    {
        public string? Attribution { get; set; }
        public string? License { get; set; }
        public string? SourceUrl { get; set; }
    }
    private sealed class RpcRow
    {
        public long Position { get; set; }
        public string? UserId { get; set; }
        public string? DisplayName { get; set; }
        public long Value { get; set; }
        public string? RankKey { get; set; }
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
