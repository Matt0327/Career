using System.Text.RegularExpressions;
using Callsign.Server;
using Callsign.Server.Auth;
using Callsign.Server.Data;
using Callsign.Server.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Dev DB is SQLite (free, zero infra). Override ConnectionStrings:Server (env: ConnectionStrings__Server)
// with a Postgres string at deploy — the model is provider-agnostic.
string conn = builder.Configuration.GetConnectionString("Server")
              ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "callsign-server.db")}";
builder.Services.AddDbContext<ServerDbContext>(options => options.UseSqlite(conn));

// The desktop client's WebView2 UI calls this API from its own origin, so the browser enforces CORS.
// Dev: allow any origin. Deploy: lock this to the app's real origin(s).
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();
app.UseCors();

// Create the schema on first run. Dev uses EnsureCreated; migrations land before we deploy.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.EnsureCreated();

// ── helpers ──────────────────────────────────────────────────────────────────────────────────────
static string IsoNow() => DateTimeOffset.UtcNow.ToString("O");
static ProfileDto ToProfile(AppUser u) => new(u.Id.ToString(), u.Email, u.DisplayName, u.CreatedAt.ToString("O"));
static bool LooksLikeEmail(string s) => Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

// Resolve the Authorization: Bearer token to a user, or null. Expired tokens are dropped lazily.
static async Task<AppUser?> ResolveUserAsync(HttpContext http, ServerDbContext db)
{
    string? header = http.Request.Headers.Authorization;
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;
    string hash = Tokens.HashOf(header["Bearer ".Length..].Trim());
    var token = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
    if (token is null) return null;
    if (token.ExpiresAt < DateTimeOffset.UtcNow) { db.Tokens.Remove(token); await db.SaveChangesAsync(); return null; }
    return await db.Users.FindAsync(token.UserId);
}

static async Task<(string Raw, AuthToken Row)> IssueTokenAsync(ServerDbContext db, AppUser user, string? device)
{
    var (raw, hash) = Tokens.New();
    var row = new AuthToken { UserId = user.Id, TokenHash = hash, Device = Trim(device, 120) };
    db.Tokens.Add(row);
    await db.SaveChangesAsync();
    return (raw, row);
}

static string? Trim(string? s, int max) =>
    string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);

// Resolve a signed-in ADMIN (moderates the image index), or null.
static async Task<AppUser?> ResolveAdminAsync(HttpContext http, ServerDbContext db)
{
    var user = await ResolveUserAsync(http, db);
    return user is { IsAdmin: true } ? user : null;
}

static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

// Sniff an image's type from its magic bytes; null if it isn't a format we accept.
static string? DetectImageType(byte[] d)
{
    if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
    if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
    if (d.Length >= 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 &&
        d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "image/webp";
    return null;
}

// ── health ─────────────────────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "callsign-server", time = IsoNow() }));

// ── accounts ─────────────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/register", async (RegisterRequest req, ServerDbContext db, HttpContext http) =>
{
    string email = (req.Email ?? "").Trim().ToLowerInvariant();
    string name = (req.DisplayName ?? "").Trim();
    if (!LooksLikeEmail(email)) return Results.BadRequest(new { error = "Enter a valid email address." });
    if (name.Length is < 2 or > 40) return Results.BadRequest(new { error = "Display name must be 2–40 characters." });
    if ((req.Password ?? "").Length < 8) return Results.BadRequest(new { error = "Password must be at least 8 characters." });
    if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict(new { error = "That email is already registered." });

    bool isFirstUser = !await db.Users.AnyAsync();   // the first account bootstraps the image-index moderator
    var user = new AppUser { Email = email, DisplayName = name, PasswordHash = Passwords.Hash(req.Password!), IsAdmin = isFirstUser };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    var (token, _) = await IssueTokenAsync(db, user, http.Request.Headers.UserAgent);
    return Results.Ok(new AuthResponse(token, ToProfile(user)));
});

app.MapPost("/api/auth/login", async (LoginRequest req, ServerDbContext db, HttpContext http) =>
{
    string email = (req.Email ?? "").Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null || !Passwords.Verify(req.Password ?? "", user.PasswordHash))
        return Results.Json(new { error = "Wrong email or password." }, statusCode: StatusCodes.Status401Unauthorized);
    var (token, _) = await IssueTokenAsync(db, user, http.Request.Headers.UserAgent);
    return Results.Ok(new AuthResponse(token, ToProfile(user)));
});

app.MapGet("/api/me", async (ServerDbContext db, HttpContext http) =>
{
    var user = await ResolveUserAsync(http, db);
    return user is null ? Results.Unauthorized() : Results.Ok(ToProfile(user));
});

app.MapPost("/api/auth/logout", async (ServerDbContext db, HttpContext http) =>
{
    string? header = http.Request.Headers.Authorization;
    if (!string.IsNullOrWhiteSpace(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        string hash = Tokens.HashOf(header["Bearer ".Length..].Trim());
        var token = await db.Tokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is not null) { db.Tokens.Remove(token); await db.SaveChangesAsync(); }
    }
    return Results.Ok(new { ok = true });
});

// ── cloud saves (latest-wins blob per user) ────────────────────────────────────────────────────
app.MapGet("/api/save/meta", async (ServerDbContext db, HttpContext http) =>
{
    var user = await ResolveUserAsync(http, db);
    if (user is null) return Results.Unauthorized();
    var save = await db.Saves.FirstOrDefaultAsync(s => s.UserId == user.Id);
    return Results.Ok(save is null
        ? new SaveMetaDto(false, 0, null, null)
        : new SaveMetaDto(true, save.SizeBytes, save.Device, save.UpdatedAt.ToString("O")));
});

app.MapGet("/api/save", async (ServerDbContext db, HttpContext http) =>
{
    var user = await ResolveUserAsync(http, db);
    if (user is null) return Results.Unauthorized();
    var save = await db.Saves.FirstOrDefaultAsync(s => s.UserId == user.Id);
    return save is null
        ? Results.NotFound(new { error = "No cloud save yet." })
        : Results.File(save.Data, "application/octet-stream", "callsign-save.db");
});

app.MapPut("/api/save", async (ServerDbContext db, HttpContext http) =>
{
    var user = await ResolveUserAsync(http, db);
    if (user is null) return Results.Unauthorized();

    using var buffer = new MemoryStream();
    await http.Request.Body.CopyToAsync(buffer);
    byte[] data = buffer.ToArray();
    if (data.Length == 0) return Results.BadRequest(new { error = "Empty save." });
    if (data.Length > 64 * 1024 * 1024) return Results.BadRequest(new { error = "Save exceeds the 64 MB limit." });

    var save = await db.Saves.FirstOrDefaultAsync(s => s.UserId == user.Id);
    if (save is null) { save = new CloudSave { UserId = user.Id }; db.Saves.Add(save); }
    save.Data = data;
    save.SizeBytes = data.Length;
    save.Device = Trim(http.Request.Headers.UserAgent, 120);
    save.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new SaveMetaDto(true, save.SizeBytes, save.Device, save.UpdatedAt.ToString("O")));
});

// ── aircraft image index (keyed by AircraftType.Key, e.g. "C172") ────────────────────────────────
// Reads are public (images are meant to be seen); submitting needs an account; approving needs an admin.

app.MapGet("/api/images/{key}", async (string key, ServerDbContext db) =>
{
    var image = await BestApproved(db, NormalizeKey(key));
    return image is null ? Results.NotFound() : Results.File(image.Data, image.ContentType);
});

app.MapGet("/api/images/{key}/meta", async (string key, ServerDbContext db) =>
{
    var image = await BestApproved(db, NormalizeKey(key));
    return Results.Ok(image is null
        ? new { exists = false, attribution = (string?)null, license = (string?)null, sourceUrl = (string?)null }
        : new { exists = true, attribution = (string?)image.Attribution, license = (string?)image.License, sourceUrl = image.SourceUrl });
});

app.MapPost("/api/images/{key}", async (string key, string? license, string? attribution, string? sourceUrl, ServerDbContext db, HttpContext http) =>
{
    var user = await ResolveUserAsync(http, db);
    if (user is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(license) || string.IsNullOrWhiteSpace(attribution))
        return Results.BadRequest(new { error = "Every image needs a license and attribution." });

    using var buffer = new MemoryStream();
    await http.Request.Body.CopyToAsync(buffer);
    byte[] data = buffer.ToArray();
    if (data.Length == 0) return Results.BadRequest(new { error = "Empty image." });
    if (data.Length > 8 * 1024 * 1024) return Results.BadRequest(new { error = "Image exceeds the 8 MB limit." });
    var contentType = DetectImageType(data);
    if (contentType is null) return Results.BadRequest(new { error = "Only JPEG, PNG, or WebP images are accepted." });

    var image = new AircraftImage
    {
        Key = NormalizeKey(key),
        Data = data,
        ContentType = contentType,
        License = license.Trim(),
        Attribution = attribution.Trim(),
        SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(),
        SubmittedByUserId = user.Id,
        Status = ImageStatus.Pending,
    };
    db.Images.Add(image);
    await db.SaveChangesAsync();
    return Results.Ok(new { id = image.Id, status = "pending" });
});

app.MapGet("/api/images/pending", async (ServerDbContext db, HttpContext http) =>
{
    var admin = await ResolveAdminAsync(http, db);
    if (admin is null) return Results.Unauthorized();
    var pending = await db.Images
        .Where(i => i.Status == ImageStatus.Pending)
        .OrderBy(i => i.CreatedAt)
        .Select(i => new { id = i.Id, key = i.Key, license = i.License, attribution = i.Attribution, sourceUrl = i.SourceUrl, createdAt = i.CreatedAt.ToString("O") })
        .ToListAsync();
    return Results.Ok(pending);
});

app.MapPost("/api/images/{id:guid}/moderate", async (Guid id, ModerateRequest req, ServerDbContext db, HttpContext http) =>
{
    var admin = await ResolveAdminAsync(http, db);
    if (admin is null) return Results.Unauthorized();
    var image = await db.Images.FindAsync(id);
    if (image is null) return Results.NotFound();
    image.Status = req.Approve ? ImageStatus.Approved : ImageStatus.Rejected;
    await db.SaveChangesAsync();
    return Results.Ok(new { id = image.Id, status = image.Status.ToString().ToLowerInvariant() });
});

// The preferred approved image for a key: highest SortRank, then newest.
static Task<AircraftImage?> BestApproved(ServerDbContext db, string key) =>
    db.Images.Where(i => i.Key == key && i.Status == ImageStatus.Approved)
             .OrderByDescending(i => i.SortRank).ThenByDescending(i => i.CreatedAt)
             .FirstOrDefaultAsync();

app.Run();
