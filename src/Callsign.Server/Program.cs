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

    var user = new AppUser { Email = email, DisplayName = name, PasswordHash = Passwords.Hash(req.Password!) };
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

app.Run();
