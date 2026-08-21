using Callsign.Core.Achievements;
using Callsign.Core.Aircraft;
using Callsign.Core.Airline;
using System.Text.Json;
using Callsign.Core.Airports;
using Callsign.Core.Campaigns;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Callsign.SimConnect;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace Callsign.Host;

/// <summary>
/// Builds the fully-configured Callsign web application — services, middleware, the REST API, the
/// telemetry WebSocket, and static UI serving. Shared by the standalone Host exe (which runs it) and
/// the desktop shell (which starts it in-process and points a WebView2 at it). Configuration
/// (<c>Db:Path</c>, <c>Ui:Path</c>, <c>urls</c>) comes from <paramref name="args"/> / env / appsettings.
/// </summary>
public static class CallsignWebApp
{
    public static WebApplication Build(string[] args)
    {
        // Money is USD. The app runs under InvariantGlobalization (no ICU), whose currency symbol is "¤", so a
        // ":C0" in a service error message would read "¤400,000". Clone the invariant culture, give it a "$"
        // symbol, and make it the process default — every currency format then renders as dollars, with no ICU
        // dependency and without touching each call site. (The UI formats its own money in JS.)
        var money = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        money.NumberFormat.CurrencySymbol = "$";
        money.NumberFormat.CurrencyNegativePattern = 1; // -$1,234 rather than ($1,234)
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = money;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = money;

        var builder = WebApplication.CreateBuilder(args);

        // --- Database: one SQLite file (path overridable via config "Db:Path" / env Db__Path) ---
        var dbPath = builder.Configuration["Db:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "callsign.db");
        // Apply a restore staged in a previous session BEFORE anything opens the file (the live DB can't
        // be swapped while held open), moving the current save aside rather than destroying it.
        SaveService.ApplyPendingRestore(dbPath);
        builder.Services.AddDbContext<CallsignDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddSingleton(new SaveService(dbPath));

        // --- Callsign Cloud gateway: the app reaches Callsign Cloud (Supabase) THROUGH the Host (no CORS,
        //     the session lives in the Host not the browser, save transfers stream server-to-server). URL +
        //     anon key are overridable via config "Supabase:Url" / "Supabase:AnonKey". The anon key is a
        //     public, ship-safe value — Row-Level Security is what actually protects data. ---
        var supabaseUrl = builder.Configuration["Supabase:Url"] ?? "https://ewmjygogceutvgalnlns.supabase.co";
        var supabaseAnonKey = builder.Configuration["Supabase:AnonKey"]
            ?? "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImV3bWp5Z29nY2V1dHZnYWxubG5zIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY5Njc0OTAsImV4cCI6MjEwMjU0MzQ5MH0.-UhY1YC2chis3cc1uDS3oq9LXolk3ivNZcBmQHTB3nE";
        var cloudSessionPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "cloud-session.json");
        builder.Services.AddSingleton(new Callsign.Host.Cloud.SupabaseConfig(supabaseUrl, supabaseAnonKey));
        builder.Services.AddSingleton(new Callsign.Host.Cloud.CloudSession(cloudSessionPath));
        builder.Services.AddHttpClient<Callsign.Host.Cloud.CloudGateway>(c => c.BaseAddress = new Uri(supabaseUrl));
        var imageCacheDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "image-cache");
        builder.Services.AddSingleton(new Callsign.Host.Cloud.AircraftImageCache(imageCacheDir));

        // --- Web UI: the Vite build output (path overridable via config "Ui:Path" / env Ui__Path) ---
        var uiPath = builder.Configuration["Ui:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");

        // --- Singletons ---
        builder.Services.AddSingleton<IClock, SystemClock>();
        // Live weather (Phase 9b) is the one config-driven knob set: bind LiveWeather:* into the injected
        // EconomyConfig so the runtime guards (in AviationWeatherSource + WeatherProvider) read the SAME toggle
        // the DI registration gate below reads — otherwise the feature is inert even with the source registered.
        var economyConfig = EconomyConfig.Default with
        {
            LiveWeatherEnabled = builder.Configuration.GetValue<bool?>("LiveWeather:Enabled") ?? EconomyConfig.Default.LiveWeatherEnabled,
            LiveWeatherFeedsMarket = builder.Configuration.GetValue<bool?>("LiveWeather:FeedsMarket") ?? EconomyConfig.Default.LiveWeatherFeedsMarket,
        };
        builder.Services.AddSingleton(economyConfig);
        builder.Services.AddSingleton<IJobSource>(sp =>
        {
            var cfg = sp.GetRequiredService<EconomyConfig>();
            // The board mixes the full mission roster (Phase 3e), each type by its catalogue share, plus the
            // Phase-11d contract on-ramp (invented-carrier overflow work) as a modest extra share.
            var sources = MissionCatalog.Generated
                .Select(d => ((IJobSource)new MissionJobSource(d, cfg), d.BoardShare))
                .Append(((IJobSource)new ContractJobSource(cfg), cfg.ContractCarrierBoardShare))
                .ToArray();
            return new CompositeJobSource(sources);
        });
        builder.Services.AddSingleton<AircraftScanner>();
        builder.Services.AddSingleton<ISimTelemetrySource>(sp =>
            SimTelemetryFactory.Create(sp.GetRequiredService<ILoggerFactory>().CreateLogger("Telemetry")));
        builder.Services.AddSingleton<FlightSessionService>();

        // --- Scoped services (per request) ---
        builder.Services.AddScoped<AirportRepository>();
        builder.Services.AddScoped<LedgerService>();
        builder.Services.AddScoped<NewGameService>();
        builder.Services.AddScoped<JobAssignmentService>();
        builder.Services.AddScoped<SettlementService>();
        builder.Services.AddScoped<AircraftRosterService>();
        builder.Services.AddScoped<AircraftDealerService>();
        builder.Services.AddScoped<JobBoardService>();
        builder.Services.AddScoped<OperationsService>();
        builder.Services.AddScoped<BaseService>();
        builder.Services.AddScoped<GameSetupService>();
        builder.Services.AddScoped<TradeService>();
        builder.Services.AddScoped<QualificationService>();
        builder.Services.AddScoped<CheckFlightService>();
        builder.Services.AddScoped<LoanService>();
        builder.Services.AddScoped<FinanceService>();
        builder.Services.AddScoped<InsuranceService>();
        builder.Services.AddScoped<RouteService>();
        builder.Services.AddScoped<CertificateService>();
        builder.Services.AddScoped<ClientService>();
        builder.Services.AddScoped<ProgressMetricsService>();
        builder.Services.AddScoped<AchievementService>();
        builder.Services.AddScoped<CampaignService>();
        builder.Services.AddScoped<AirlineService>();
        builder.Services.AddSingleton<MarketService>(); // pure pricing (IClock + EconomyConfig)
        builder.Services.AddSingleton<Callsign.Core.World.WorldOracle>(); // pure synthetic world model (Phase 8)

        // Live weather (Phase 9b): a real-METAR seam ABOVE the pure WorldOracle. When OFF (the default), a
        // NullWeatherSource makes every read synthetic — "off" and "feed down" are the same code path. The live
        // adapter is Host-only (owns HttpClient), so Core never gains a System.Net.Http reference (firewall lock).
        bool liveWeatherOn = economyConfig.LiveWeatherEnabled; // same toggle the runtime guards read (bound above)
        if (liveWeatherOn)
        {
            builder.Services.AddHttpClient(Callsign.Host.World.AviationWeatherSource.HttpClientName, c =>
            {
                c.BaseAddress = new Uri(builder.Configuration["LiveWeather:BaseUrl"] ?? EconomyConfig.Default.LiveWeatherBaseUrl);
                c.Timeout = EconomyConfig.Default.LiveWeatherTimeout;
                c.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["LiveWeather:UserAgent"] ?? EconomyConfig.Default.LiveWeatherUserAgent);
            });
            builder.Services.AddSingleton<Callsign.Core.World.IWeatherSource, Callsign.Host.World.AviationWeatherSource>();
        }
        else
        {
            builder.Services.AddSingleton<Callsign.Core.World.IWeatherSource>(Callsign.Core.World.NullWeatherSource.Instance);
        }
        builder.Services.AddSingleton<Callsign.Core.World.WeatherProvider>(); // resolver: live-if-fresh-else-synthetic

        var app = builder.Build();
        app.UseWebSockets();

        // Same-origin guard (security): the SPA is served from this very Host, so the API needs NO cross-origin
        // access — and must not grant it. Without this, any web page the user has open could drive the loopback
        // API (read the signed-in account, push/pull the cloud save, wipe the career) via CSRF. We reject any
        // /api request that carries a cross-origin Origin header; same-origin calls (no Origin, or Origin == this
        // Host) and non-browser tools (no Origin) pass. The WebSocket and static UI are unaffected.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) &&
                    !origin.EndsWith("//" + ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Cross-origin API access is not allowed." });
                    return;
                }
            }
            await next();
        });

        // --- Serve the built React UI (if present). API + WebSocket routes are matched first;
        //     any other path falls back to index.html so client-side navigation works. ---
        if (Directory.Exists(uiPath))
        {
            var ui = new PhysicalFileProvider(Path.GetFullPath(uiPath));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });
        }

        // Create/upgrade the schema through EF migrations, so a shipped install survives a schema
        // change across app updates instead of having its save wiped. (Tests still use EnsureCreated
        // on throwaway DBs.)
        using (var scope = app.Services.CreateScope())
            PrepareDatabase(scope.ServiceProvider.GetRequiredService<CallsignDbContext>());

        // Start streaming telemetry into the flight session (live SimConnect on the Windows build,
        // synthetic source on the portable build or when SimConnect isn't available).
        _ = app.Services.GetRequiredService<FlightSessionService>().StartAsync();

        // Reopen reconciliation: book any autonomous standing-order trips + wages that accrued while
        // the app was closed, so the company keeps ticking (Phase 2d).
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var pilot = db.Pilots.FirstOrDefault();
            if (pilot is not null)
            {
                try
                {
                    scope.ServiceProvider.GetRequiredService<OperationsService>().ReconcileAsync(pilot.CompanyId).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // A reconcile fault must NEVER brick startup — an unhandled throw here would fail every
                    // launch until the DB were hand-fixed. Log and start anyway; the next reconcile (the Ops
                    // "Process now" button, or the next launch) picks the accrued window back up.
                    app.Logger.LogError(ex, "Startup reconcile failed; starting without it.");
                }
            }
        }

        MapEndpoints(app, uiPath);
        return app;
    }

    // Bring the database up to the current schema before the app serves a request. A fresh DB gets the
    // full InitialCreate; an already-migrated DB is a no-op (its save intact). But a pre-release DB whose
    // schema and migration history don't reconcile — a pre-migrations EnsureCreated DB (tables, no
    // history), OR one with a history table that never recorded InitialCreate — makes Migrate() try to
    // re-create existing tables and throw "table already exists". We catch every shape of that: proactively
    // for the obvious case, and as a safety net around Migrate() itself. Such a disposable pre-release save
    // is set aside (a .bak) and rebuilt clean rather than crashing the app on every launch.
    // Exposed for the startup-robustness tests.
    public static void PrepareDatabase(CallsignDbContext db)
    {
        if (IsLegacyEnsureCreatedDatabase(db))
            RetireLegacyDatabase(db);
        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex) when (IsSchemaAlreadyExists(ex))
        {
            RetireLegacyDatabase(db); // preserve the old bytes, then build the schema fresh
            db.Database.Migrate();
        }
    }

    // True for the "table … already exists" family of SQLite migration failures — the signature of a
    // schema that's present but whose migration history can't account for it.
    private static bool IsSchemaAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is Microsoft.Data.Sqlite.SqliteException && e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Move a legacy (pre-migrations) database aside so Migrate() can build a fresh one — WITHOUT
    // destroying the old bytes. The file is renamed to callsign.db.bak-&lt;timestamp&gt; (with its
    // -wal/-shm sidecars), so anyone who had real progress can still recover it. Falls back to a hard
    // delete only if the move fails, so a locked/odd file can never block startup.
    private static void RetireLegacyDatabase(CallsignDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var path = conn.DataSource; // the SQLite file backing this context
        try
        {
            conn.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // drop any pooled handle on the file
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                db.Database.EnsureDeleted();
                return;
            }
            var backup = $"{path}.bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = path + suffix;
                if (File.Exists(src))
                    File.Move(src, backup + suffix);
            }
        }
        catch
        {
            // Rename failed (locked / permissions) — fall back to the original behaviour so we still boot.
            try { db.Database.EnsureDeleted(); } catch { /* Migrate() will surface any real problem next */ }
        }
    }

    // True if the DB file exists with the app's own tables but no EF migrations history — the signature
    // of a pre-migrations build (EnsureCreated). Migrate() can't run against it (the tables it wants to
    // create already exist), and for a disposable pre-release save the right move is to rebuild it clean.
    // The probe is best-effort: any failure returns false so a real DB is never dropped by mistake.
    private static bool IsLegacyEnsureCreatedDatabase(CallsignDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        try
        {
            if (wasClosed)
                conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT " +
                "(SELECT count(*) FROM sqlite_master WHERE type='table' AND name='AircraftTypes'), " +
                "(SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory')";
            using var reader = cmd.ExecuteReader();
            return reader.Read() && reader.GetInt64(0) > 0 && reader.GetInt64(1) == 0;
        }
        catch
        {
            return false; // never block startup — worst case is the original Migrate() path runs
        }
        finally
        {
            if (wasClosed && conn.State == System.Data.ConnectionState.Open)
                conn.Close();
        }
    }

    // Look up human airport names for a set of idents (so the UI can show "EHRD · Rotterdam The Hague").
    private static async Task<Dictionary<string, string>> AirportNamesAsync(CallsignDbContext db, IEnumerable<string> idents)
    {
        var set = idents.Distinct().ToList();
        if (set.Count == 0)
            return new Dictionary<string, string>();
        return await db.Airports.Where(a => set.Contains(a.Ident)).ToDictionaryAsync(a => a.Ident, a => a.Name);
    }

    // Full airport rows by ident — lat/lon, class, and longest runway for the map + arrival detail.
    private static async Task<Dictionary<string, Callsign.Core.Domain.Airport>> AirportsByIdentAsync(CallsignDbContext db, IEnumerable<string> idents)
    {
        var set = idents.Distinct().ToList();
        if (set.Count == 0)
            return new Dictionary<string, Callsign.Core.Domain.Airport>();
        return await db.Airports.Where(a => set.Contains(a.Ident)).ToDictionaryAsync(a => a.Ident, a => a);
    }

    private static void MapEndpoints(WebApplication app, string uiPath)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        // --- Callsign Cloud: accounts + cloud saves, proxied through the local Host ---
        app.MapGet("/api/cloud/status", (Callsign.Host.Cloud.CloudGateway cloud) =>
            Results.Ok(new
            {
                signedIn = cloud.Session.IsSignedIn,
                email = cloud.Session.Email,
                displayName = cloud.Session.DisplayName,
                baseUrl = cloud.BaseUrl,
            }));

        app.MapPost("/api/cloud/register", async (Callsign.Host.Cloud.CloudCredsDto req, Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            var r = await cloud.RegisterAsync(req.Email ?? "", req.DisplayName ?? "", req.Password ?? "");
            return r.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = r.Error });
        });

        app.MapPost("/api/cloud/login", async (Callsign.Host.Cloud.CloudCredsDto req, Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            var r = await cloud.LoginAsync(req.Email ?? "", req.Password ?? "");
            return r.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = r.Error });
        });

        app.MapPost("/api/cloud/logout", async (Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            await cloud.LogoutAsync();
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/cloud/save/meta", async (Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            if (!cloud.Session.IsSignedIn) return Results.Unauthorized();
            var meta = await cloud.SaveMetaAsync();
            return Results.Ok(meta ?? new Callsign.Host.Cloud.CloudSaveMeta(false, 0, null, null));
        });

        app.MapPost("/api/cloud/push", async (Callsign.Host.Cloud.CloudGateway cloud, CallsignDbContext db) =>
        {
            var r = await cloud.PushAsync(db);
            return r.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = r.Error });
        });

        app.MapPost("/api/cloud/pull", async (Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            var r = await cloud.PullAsync();
            return r.Ok ? Results.Ok(new { ok = true, staged = true }) : Results.BadRequest(new { error = r.Error });
        });

        // Leaderboards: the app gathers the player's real standing (same numbers as achievements/campaigns)
        // and pushes it; the boards are read back through the Host so the UI only ever talks to localhost.
        app.MapPost("/api/cloud/leaderboard/submit", async (CallsignDbContext db, ProgressMetricsService metrics, Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            if (!cloud.Session.IsSignedIn) return Results.Unauthorized();
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.BadRequest(new { error = "Start a career first." });
            var m = await metrics.SnapshotAsync(pilot.CompanyId, pilot.Id);
            var stats = new Callsign.Host.Cloud.LeaderboardSubmit(m.NetWorthCents, m.Flights, m.ReputationMilli, pilot.Xp, pilot.Rank.ToString());
            var r = await cloud.SubmitLeaderboardAsync(stats);
            return r.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = r.Error });
        });

        app.MapGet("/api/cloud/leaderboard", async (string? board, int? limit, Callsign.Host.Cloud.CloudGateway cloud) =>
            Results.Ok(await cloud.GetLeaderboardAsync(board ?? "networth", Math.Clamp(limit ?? 100, 1, 500))));

        // Report the aircraft types this app knows into the shared community catalog (facts only).
        app.MapPost("/api/cloud/aircraft/report", async (CallsignDbContext db, Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            if (!cloud.Session.IsSignedIn) return Results.Ok(new { reported = 0 });
            var types = await db.AircraftTypes.AsNoTracking()
                .Select(t => new { t.Key, t.CanonicalName, t.Manufacturer, t.Category })
                .ToListAsync();
            var items = types
                .Select(t => (object)new { key = t.Key, name = t.CanonicalName, manufacturer = t.Manufacturer, category = t.Category.ToString() })
                .ToList();
            if (items.Count > 0) await cloud.ReportAircraftAsync(items);
            return Results.Ok(new { reported = items.Count });
        });

        app.MapPost("/api/game/new", async (NewCareerRequest req, GameSetupService setup) =>
        {
            var (company, pilot) = await setup.StartNewCareerAsync(
                string.IsNullOrWhiteSpace(req.Name) ? "New Pilot" : req.Name!,
                string.IsNullOrWhiteSpace(req.HomeIcao) ? "EHAM" : req.HomeIcao!,
                req.StartingCash ?? 10_000m,
                string.IsNullOrWhiteSpace(req.StarterTypeCode) ? null : req.StarterTypeCode,
                string.IsNullOrWhiteSpace(req.Edition) ? null : req.Edition,
                string.IsNullOrWhiteSpace(req.AvatarKey) ? null : req.AvatarKey);
            return Results.Ok(new { pilotId = pilot.Id, companyId = company.Id, home = pilot.HomeIcao });
        });

        app.MapGet("/api/game/state", async (CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null)
                return Results.NotFound(new { error = "No career. POST /api/game/new first." });
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == pilot.CompanyId);
            if (company is null)
                return Results.NotFound(new { error = "Career data is incomplete (no account). Restore a backup or start a new career." });
            var flights = await db.Flights.CountAsync();
            return Results.Ok(new StateDto(pilot.Name, pilot.Rank.ToString(), pilot.Xp, pilot.ReputationMilli,
                pilot.CurrentIcao, pilot.HomeIcao, company.CashCents, company.Cash, flights, pilot.AvatarKey));
        });

        // Reputation (Phase 3f): the current standing + the recent log so the drift is legible.
        app.MapGet("/api/reputation", async (int? limit, CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var events = await db.ReputationEvents.Where(e => e.PilotId == pilot.Id)
                .OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 15, 1, 100)).ToListAsync();
            return Results.Ok(new ReputationDto(pilot.ReputationMilli,
                events.Select(e => new ReputationEventDto(e.DeltaMilli, e.BalanceMilli, e.Reason, e.At)).ToList()));
        });

        // The rank ladder (Phase 3a), flagged against the player's current XP/rank — self-documenting.
        app.MapGet("/api/ranks", async (CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            int xp = pilot?.Xp ?? 0;
            var current = pilot?.Rank ?? PilotRank.Trainee;
            return Results.Ok(RankTiers.All.Select(t => new RankTierDto(
                t.Rank.ToString(), t.DisplayName, t.Description, t.MinXp, xp >= t.MinXp, t.Rank == current)));
        });

        app.MapPost("/api/jobs/refresh", async (string? origin, int? count, CallsignDbContext db, JobBoardService board) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var icao = string.IsNullOrWhiteSpace(origin) ? pilot.CurrentIcao : origin!;
            // Phase 11c — pass the company so a board at one of our HUBS (a base) reflects the airline's operating
            // reputation (more offers, better pay), frozen at posting. Off-hub boards are unaffected.
            var n = await board.RefreshAsync(icao, pilot.Rank, count ?? 8, Environment.TickCount, pilot.CompanyId);
            return Results.Ok(new { origin = icao, generated = n });
        });

        app.MapGet("/api/jobs", async (string? origin, CallsignDbContext db, JobBoardService board, EconomyConfig cfg, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var icao = string.IsNullOrWhiteSpace(origin) ? pilot.CurrentIcao : origin!;
            var jobs = await board.GetAvailableAsync(icao);
            var ports = await AirportsByIdentAsync(db, jobs.SelectMany(j => new[] { j.OriginIcao, j.DestIcao }));
            // Operating-certificate gate (Phase 8e): premium categories lock unless the company holds a valid cert.
            var validCerts = (await db.OperatingCertificates
                .Where(c => c.CompanyId == pilot.CompanyId && c.ExpiresAt > clock.UtcNow).Select(c => c.Kind).ToListAsync()).ToHashSet();
            // Your standing with the clients behind these offers (Phase 8d) — so the board can show the repeat
            // premium a loyal client will pay, turning "whose job to fly" into a real choice.
            var clientKeys = jobs.Where(j => j.ClientKey != null).Select(j => j.ClientKey!).Distinct().ToList();
            // Loyalty is decayed to now (Phase 8d-2) so the board shows the client's CURRENT standing — a bond
            // you've let cool pays a smaller premium than the peak it once reached.
            var loyalty = (await db.Clients
                .Where(c => c.CompanyId == pilot.CompanyId && clientKeys.Contains(c.ClientKey)).ToListAsync())
                .ToDictionary(c => c.ClientKey, c => cfg.DecayedLoyaltyMilli(c.LoyaltyMilli, clock.UtcNow - (c.LastJobAt ?? c.UpdatedAt)));
            return Results.Ok(jobs.Select(j =>
            {
                // Shown on the board, but locked with the reason (rank — 3b, or reputation — 3e/3f).
                var def = MissionCatalog.Def(j.Type);
                bool rankLocked = pilot.Rank < j.RequiredRank;
                bool repLocked = pilot.ReputationMilli < def.MinReputationMilli;
                var reqCert = Callsign.Core.Economy.CertificateCatalog.RequiredFor(j.Type);
                bool certLocked = reqCert is { } rk && !validCerts.Contains(rk);
                var reqName = RankTiers.Def(j.RequiredRank).DisplayName;
                string? reason = rankLocked ? $"Requires {reqName}"
                    : repLocked ? $"Requires reputation {def.MinReputationMilli / 1000.0:0.0}"
                    : certLocked ? $"Requires {Callsign.Core.Economy.CertificateCatalog.Def(reqCert!.Value).DisplayName}"
                    : null;
                ports.TryGetValue(j.OriginIcao, out var o);
                ports.TryGetValue(j.DestIcao, out var d);
                var kind = d?.Kind ?? AirportKind.Unknown;
                int clientLoyalty = j.ClientKey != null && loyalty.TryGetValue(j.ClientKey, out var lm) ? lm : 0;
                long expBonus = (long)Math.Round(j.RewardCents * (decimal)cfg.ClientLoyaltyBonusPct(clientLoyalty));
                return new JobDto(j.Id, j.Type.ToString(), j.OriginIcao, j.DestIcao,
                    d?.Name ?? j.DestIcao, j.Commodity, j.WeightLbs, j.Pax, j.DistanceNm,
                    j.RewardCents, j.Xp, reqName, rankLocked || repLocked || certLocked, reason, j.ExpiresAt,
                    o?.Latitude ?? 0, o?.Longitude ?? 0, d?.Latitude ?? 0, d?.Longitude ?? 0,
                    kind.ToString(), d?.LongestRunwayFt, cfg.LandingFeeCents(kind),
                    j.ClientName, clientLoyalty, expBonus,
                    j.HubRepBonusCents);
            }));
        });

        app.MapPost("/api/jobs/{id:guid}/accept", async (Guid id, CallsignDbContext db, JobAssignmentService svc) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var a = await svc.AcceptAsync(id, pilot.CompanyId, pilot.Id);
                return Results.Ok(new { assignmentId = a.Id, dest = a.DestIcao, rewardQuoteCents = a.RewardQuoteCents });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/assignments", async (CallsignDbContext db, EconomyConfig cfg) =>
        {
            var list = await db.JobAssignments.Where(a => a.Status == AssignmentStatus.Accepted).ToListAsync();
            var ports = await AirportsByIdentAsync(db, list.SelectMany(a => new[] { a.OriginIcao, a.DestIcao }));
            return Results.Ok(list.Select(a =>
            {
                ports.TryGetValue(a.OriginIcao, out var o);
                ports.TryGetValue(a.DestIcao, out var d);
                var kind = d?.Kind ?? AirportKind.Unknown;
                return new AssignmentDto(a.Id, a.Type.ToString(), a.OriginIcao, a.DestIcao,
                    d?.Name ?? a.DestIcao, a.Commodity, a.WeightLbs, a.Pax, a.DistanceNm, a.RewardQuoteCents, a.XpQuote, a.Status.ToString(),
                    o?.Latitude ?? 0, o?.Longitude ?? 0, d?.Latitude ?? 0, d?.Longitude ?? 0,
                    kind.ToString(), d?.LongestRunwayFt, cfg.LandingFeeCents(kind), a.DeadlineAt);
            }));
        });

        app.MapPost("/api/assignments/{id:guid}/settle", async (Guid id, FlightResultDto dto, SettlementService svc) =>
        {
            var record = new Callsign.Core.Flight.FlightRecord(
                dto.AircraftTitle, dto.DepartedAt, dto.ArrivedAt, dto.TouchdownFpm, dto.MaxAltitudeFt,
                dto.DepartureLat, dto.DepartureLon, dto.ArrivalLat, dto.ArrivalLon, dto.DistanceNm, dto.FuelUsedLbs, []);
            try
            {
                var r = await svc.SettleAsync(id, record);
                return Results.Ok(new SettlementDto(r.FlightId, r.PayoutCents, r.XpAwarded, r.PayloadMatched,
                    r.PromotedTo is { } pr ? RankTiers.Def(pr).DisplayName : null,
                    r.Breakdown.Lines.Select(l => new PayoutLineDto(l.Label, l.AmountCents)).ToList()));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/roster", async (CallsignDbContext db) =>
        {
            var types = await db.AircraftTypes.ToListAsync();
            var installed = (await db.InstalledPackages.ToListAsync()).ToLookup(i => i.AircraftTypeId);
            return Results.Ok(types
                .Select(t => new RosterDto(t.Key, t.CanonicalName, t.Category.ToString(),
                    installed[t.Id].Any(i => i.IsOnDisk), t.Seats, t.UsefulLoadLbs, t.CruiseKtas, t.MinRunwayFt))
                .OrderBy(r => r.Name));
        });

        // --- Aircraft ownership (Phase 2a): buy market, hangar ---
        app.MapGet("/api/aircraft/market", async (AircraftDealerService dealer) =>
        {
            var offers = await dealer.GetOffersAsync();
            return Results.Ok(offers.Select(o => new AircraftOfferDto(
                o.Type.Id, o.Type.CanonicalName, o.Type.Category.ToString(), o.Quote.TotalCents, o.OnDisk,
                o.Type.Seats, o.Type.UsefulLoadLbs, o.Type.CruiseKtas,
                o.Quote.Factors.Select(f => new PriceFactorDto(f.Label, f.AmountCents)).ToList())));
        });

        app.MapGet("/api/aircraft", async (CallsignDbContext db, AircraftDealerService dealer, QualificationService quals, EconomyConfig cfg) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();

            var hangar = await dealer.GetHangarAsync(pilot.CompanyId);
            var held = await quals.HeldClassesAsync(pilot.Id);
            var onDisk = (await db.InstalledPackages.Where(i => i.IsOnDisk).Select(i => i.AircraftTypeId).ToListAsync()).ToHashSet();
            var ids = hangar.Select(h => h.Instance.Id).ToList();
            var ports = await AirportsByIdentAsync(db, hangar.Select(h => h.Instance.LocationIcao));

            var flightAgg = (await db.Flights
                    .Where(f => f.AircraftInstanceId != null && ids.Contains(f.AircraftInstanceId!.Value)).ToListAsync())
                .GroupBy(f => f.AircraftInstanceId!.Value)
                .ToDictionary(g => g.Key, g => (Flights: g.Count(), Dist: g.Sum(f => f.DistanceNm),
                    Fuel: g.Sum(f => f.FuelUsedLbs), Earn: g.Sum(f => f.PayoutCents)));
            var upkeepAgg = (await db.LedgerEntries
                    .Where(e => e.AircraftInstanceId != null && ids.Contains(e.AircraftInstanceId!.Value) && e.AmountCents < 0
                        && (e.Category == LedgerCategory.Repair || e.Category == LedgerCategory.Fuel
                            || e.Category == LedgerCategory.InsurancePremium)).ToListAsync())
                .GroupBy(e => e.AircraftInstanceId!.Value)
                .ToDictionary(g => g.Key, g => -g.Sum(e => e.AmountCents));
            var byInst = (await db.InsurancePolicies
                    .Where(p => p.CompanyId == pilot.CompanyId && p.Active && !p.IsDeleted && p.AircraftInstanceId != null).ToListAsync())
                .ToDictionary(p => p.AircraftInstanceId!.Value);

            return Results.Ok(hangar.Select(h =>
            {
                var inst = h.Instance; var t = h.Type;
                var reqd = QualificationClasses.ForCategory(t.Category);
                ports.TryGetValue(inst.LocationIcao, out var port);
                flightAgg.TryGetValue(inst.Id, out var fa);
                upkeepAgg.TryGetValue(inst.Id, out var upkeep);
                byInst.TryGetValue(inst.Id, out var pol);
                double hoursToService = Math.Max(0, cfg.MaintenanceIntervalHours - (inst.AirframeHours - inst.MaintenanceHoursWatermark));
                var aw = dealer.Airworthiness(inst);

                return new OwnedAircraftDto(
                    inst.Id, t.Id, inst.Tail, t.CanonicalName, t.Category.ToString(),
                    inst.LocationIcao, inst.Availability.ToString(),
                    inst.PurchasePriceCents, inst.AirframeHours,
                    inst.HullConditionMilli, inst.EngineConditionMilli,
                    dealer.MaintenanceDue(inst), dealer.MaintenanceQuoteCents(inst),
                    QualificationClasses.Def(reqd).DisplayName, held.Contains(reqd),
                    t.Manufacturer, t.IcaoTypeDesignator, t.IcaoModel, onDisk.Contains(t.Id),
                    t.Seats, t.UsefulLoadLbs, t.FuelCapacityLbs, t.CruiseKtas, t.MinRunwayFt,
                    dealer.MarketValueCents(t), dealer.ResaleValueCents(inst, t), inst.AcquiredAt,
                    port?.Latitude ?? 0, port?.Longitude ?? 0, port?.Name ?? inst.LocationIcao,
                    inst.MaintenanceHoursWatermark, cfg.MaintenanceIntervalHours, hoursToService, cfg.ConditionWearMilliPerHour,
                    pol != null, pol?.CoverageMilli, pol?.CoveredValueCents,
                    fa.Flights, fa.Dist, fa.Fuel, fa.Earn, upkeep,
                    aw.Airworthy, aw.Reason, aw.HoursTo100h, aw.DaysToAnnual, aw.InspectionQuoteCents,
                    inst.Ownership.ToString());
            }));
        });

        // The aircraft's own thumbnail from the player's MSFS install (Phase 6b) — read locally, served
        // only to them, never bundled. 404 when it's not on disk; the UI then draws an original silhouette.
        app.MapGet("/api/aircraft/type/{typeId:guid}/thumbnail", async (Guid typeId, CallsignDbContext db) =>
        {
            var pkg = await db.InstalledPackages.FirstOrDefaultAsync(p => p.AircraftTypeId == typeId && p.IsOnDisk);
            if (pkg is null || !MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp))
                return Results.NotFound();
            var path = AircraftThumbnails.TryResolve(ipp, pkg.Source, pkg.PackageFolder, pkg.AircraftFolder);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // The fuller image cascade (B1): the player's own installed thumbnail first (their real livery), then
        // the Callsign Cloud image index by the type's stable Key (cached on disk), else 404 -> UI silhouette.
        app.MapGet("/api/aircraft/type/{typeId:guid}/image", async (Guid typeId, CallsignDbContext db, Callsign.Host.Cloud.CloudGateway cloud, Callsign.Host.Cloud.AircraftImageCache cache) =>
        {
            var pkg = await db.InstalledPackages.FirstOrDefaultAsync(p => p.AircraftTypeId == typeId && p.IsOnDisk);
            if (pkg is not null && MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp))
            {
                var path = AircraftThumbnails.TryResolve(ipp, pkg.Source, pkg.PackageFolder, pkg.AircraftFolder);
                if (path is not null) return Results.File(path, "image/jpeg");
            }

            var type = await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == typeId);
            if (type is null) return Results.NotFound();

            var cached = cache.TryGet(type.Key);
            if (cached is not null) return Results.File(cached, Callsign.Host.Cloud.ImageSniff.ContentType(cached));

            var fetched = await cloud.GetTypeImageAsync(type.Key);
            if (fetched is { } img) { cache.Put(type.Key, img.Data); return Results.File(img.Data, img.ContentType); }

            return Results.NotFound();
        });

        // The credit for whatever the cascade shows: nothing for the player's own installed thumbnail
        // (their asset), else the cloud index image's attribution + license — for the on-image caption.
        app.MapGet("/api/aircraft/type/{typeId:guid}/image/meta", async (Guid typeId, CallsignDbContext db, Callsign.Host.Cloud.CloudGateway cloud) =>
        {
            var pkg = await db.InstalledPackages.FirstOrDefaultAsync(p => p.AircraftTypeId == typeId && p.IsOnDisk);
            if (pkg is not null && MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp)
                && AircraftThumbnails.TryResolve(ipp, pkg.Source, pkg.PackageFolder, pkg.AircraftFolder) is not null)
                return Results.Ok(new { attribution = (string?)null, license = (string?)null, sourceUrl = (string?)null });

            var type = await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == typeId);
            var meta = type is null ? null : await cloud.GetTypeImageMetaAsync(type.Key);
            return Results.Ok(new { attribution = meta?.Attribution, license = meta?.License, sourceUrl = meta?.SourceUrl });
        });

        app.MapPost("/api/aircraft/{id:guid}/maintain", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var cost = await dealer.MaintainAsync(pilot.CompanyId, id, idem);
                return Results.Ok(new { maintainedCents = cost });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { error = "Cash changed at the same time — try again." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/aircraft/{id:guid}/inspect", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var cost = await dealer.InspectAsync(pilot.CompanyId, id, idem);
                return Results.Ok(new { inspectedCents = cost });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { error = "Cash changed at the same time — try again." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/aircraft/buy", async (BuyAircraftRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var inst = await dealer.BuyAsync(pilot.CompanyId, req.TypeId, pilot.CurrentIcao, idem);
                return Results.Ok(new { id = inst.Id, tail = inst.Tail });
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another money movement committed first (the Company version token conflicted).
                return Results.Conflict(new { error = "Cash changed at the same time — try again." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Used-aircraft market (Phase 7g): pre-owned airframes, cheaper but flown.
        app.MapGet("/api/aircraft/used", async (CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var listings = await dealer.GetUsedMarketAsync(pilot.CompanyId.GetHashCode());
            return Results.Ok(listings.Select(l => new UsedListingDto(l.Seed, l.TypeId, l.TypeName, l.Category, l.AirframeHours, l.ConditionMilli, l.PriceCents, l.NewPriceCents)));
        });

        app.MapPost("/api/aircraft/buy-used", async (BuyUsedRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var inst = await dealer.BuyUsedAsync(pilot.CompanyId, req.TypeId, req.Seed, pilot.CurrentIcao, idem);
                return Results.Ok(new { id = inst.Id, tail = inst.Tail });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Per-airframe drill-down: full flight log, attributed ledger, and lifetime economics.
        app.MapGet("/api/aircraft/{id:guid}/history", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var inst = await db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == pilot.CompanyId && !a.IsDeleted);
            if (inst is null) return Results.NotFound(new { error = "Aircraft not found in your fleet." });
            var type = await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId);
            if (type is null) return Results.NotFound();

            var flights = await db.Flights.Where(f => f.AircraftInstanceId == id).OrderByDescending(f => f.SettledAt).ToListAsync();
            var asgIds = flights.Where(f => f.JobAssignmentId != null).Select(f => f.JobAssignmentId!.Value).Distinct().ToList();
            var asg = await db.JobAssignments.Where(a => asgIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
            var flightDtos = flights.Select(f =>
            {
                JobAssignment? a = f.JobAssignmentId != null && asg.TryGetValue(f.JobAssignmentId.Value, out var x) ? x : null;
                return new AircraftFlightDto(f.Id, a?.OriginIcao, a?.DestIcao, (a?.Type ?? MissionType.Cargo).ToString(),
                    f.DistanceNm, f.FuelUsedLbs, f.TouchdownFpm, f.PayoutCents, f.Xp, f.SettledAt);
            }).ToList();

            var ledger = await db.LedgerEntries.Where(e => e.AircraftInstanceId == id).OrderByDescending(e => e.At).ToListAsync();
            var ledgerDtos = ledger.Select(e => new AircraftLedgerDto(e.At, e.Category.ToString(), e.AmountCents, e.Description)).ToList();

            long earnings = flights.Sum(f => f.PayoutCents);
            long repair = -ledger.Where(e => e.Category == LedgerCategory.Repair && e.AmountCents < 0).Sum(e => e.AmountCents);
            long fuel = -ledger.Where(e => e.Category == LedgerCategory.Fuel && e.AmountCents < 0).Sum(e => e.AmountCents);
            long insurance = -ledger.Where(e => e.Category == LedgerCategory.InsurancePremium && e.AmountCents < 0).Sum(e => e.AmountCents);
            int n = flights.Count;
            double dist = flights.Sum(f => f.DistanceNm);
            double fuelLbs = flights.Sum(f => f.FuelUsedLbs);
            double avgFpm = n > 0 ? flights.Average(f => f.TouchdownFpm) : 0;
            long opNet = earnings - repair - fuel - insurance;
            long perFlight = n > 0 ? earnings / n : 0;
            long perHour = inst.AirframeHours > 0.05 ? (long)Math.Round(opNet / inst.AirframeHours) : 0;

            var eco = new AircraftEconomicsDto(
                inst.PurchasePriceCents ?? 0, dealer.MarketValueCents(type), dealer.ResaleValueCents(inst, type),
                earnings, fuel, repair, insurance, opNet, n, dist, fuelLbs, avgFpm, perFlight, perHour);
            return Results.Ok(new AircraftHistoryDto(inst.Id, inst.Tail, type.CanonicalName, eco, flightDtos, ledgerDtos));
        });

        // Sell an owned airframe at its resale value.
        app.MapPost("/api/aircraft/{id:guid}/sell", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { proceedsCents = await dealer.SellAsync(pilot.CompanyId, id, idem) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Ferry an idle airframe to another field.
        app.MapPost("/api/aircraft/{id:guid}/relocate", async (Guid id, RelocateAircraftRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { feeCents = await dealer.RelocateAsync(pilot.CompanyId, id, req.DestIcao, idem) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Aircraft rental (Phase 9f-1): rent a non-owned tail short-term, fly it by hand, return it ---
        app.MapGet("/api/rentals/offers", async (CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var offers = await dealer.GetRentalOffersAsync();
            return Results.Ok(offers.Select(o => new RentalOfferDto(o.TypeId, o.TypeName, o.Category, o.StickerCents, o.DailyHoldingCents, o.FlightHourCents, o.DepositCents, o.TermDays)));
        });

        app.MapGet("/api/rentals", async (CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var rentals = await dealer.GetActiveRentalsAsync(pilot.CompanyId);
            return Results.Ok(rentals.Select(r => new ActiveRentalDto(
                r.Agreement.Id, r.Tail.Id, r.Tail.Tail, r.Type.CanonicalName, r.Tail.LocationIcao,
                r.Agreement.DepositCents, r.AccruedRentCents, r.DaysLeft, r.Agreement.FlightHourRateCents, r.Agreement.HoldingPerDayCents,
                new RentalReturnQuoteDto(r.Projected.FinalRentCents, r.Projected.DamageCents, r.Projected.DamageReason, r.Projected.RefundCents))));
        });

        app.MapPost("/api/rentals", async (RentAircraftRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var inst = await dealer.RentAsync(pilot.CompanyId, req.TypeId, pilot.CurrentIcao, req.TermDays ?? 0, idem);
                return Results.Ok(new { id = inst.Id, tail = inst.Tail });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/rentals/{id:guid}/return-quote", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var q = await dealer.ReturnQuoteAsync(pilot.CompanyId, id);
            return q is null ? Results.NotFound(new { error = "Active rental not found." })
                             : Results.Ok(new RentalReturnQuoteDto(q.FinalRentCents, q.DamageCents, q.DamageReason, q.RefundCents));
        });

        app.MapPost("/api/rentals/{id:guid}/return", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { refundCents = await dealer.ReturnAsync(pilot.CompanyId, id) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Aircraft lease + rent-to-own (Phase 9f-2) ---
        app.MapGet("/api/leases/offers", async (int? termDays, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var offers = await dealer.GetLeaseOffersAsync(termDays ?? 0);
            return Results.Ok(offers.Select(o => new LeaseOfferDto(o.TypeId, o.TypeName, o.Category, o.StickerCents, o.WeeklyRateCents, o.InsuranceWeeklyCents, o.DepositCents, o.UpfrontCents, o.BuyoutCents, o.TermDays)));
        });

        app.MapGet("/api/leases", async (CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var leases = await dealer.GetActiveLeasesAsync(pilot.CompanyId);
            return Results.Ok(leases.Select(l => new ActiveLeaseDto(
                l.Agreement.Id, l.Tail.Id, l.Tail.Tail, l.Type.CanonicalName, l.Tail.LocationIcao,
                l.Agreement.DepositCents, l.WeeklyRateCents, l.InsuranceWeeklyCents, l.DaysLeft, l.BuyoutCents, l.CasualtyEligible,
                new RentalReturnQuoteDto(l.Projected.FinalRentCents, l.Projected.DamageCents, l.Projected.DamageReason, l.Projected.RefundCents))));
        });

        app.MapPost("/api/leases", async (LeaseAircraftRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var inst = await dealer.LeaseAsync(pilot.CompanyId, req.TypeId, pilot.CurrentIcao, req.TermDays ?? 0, idem);
                return Results.Ok(new { id = inst.Id, tail = inst.Tail });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/leases/{id:guid}/return-quote", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var q = await dealer.ReturnQuoteAsync(pilot.CompanyId, id);
            return q is null ? Results.NotFound(new { error = "Active lease not found." })
                             : Results.Ok(new RentalReturnQuoteDto(q.FinalRentCents, q.DamageCents, q.DamageReason, q.RefundCents));
        });

        app.MapPost("/api/leases/{id:guid}/return", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { refundCents = await dealer.ReturnAsync(pilot.CompanyId, id) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/leases/{id:guid}/buyout", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { buyoutCents = await dealer.BuyoutAsync(pilot.CompanyId, id) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/leases/{id:guid}/casualty", async (Guid id, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { deductibleCents = await dealer.CasualtyAsync(pilot.CompanyId, id) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Staff + standing orders (Phase 2d) ---
        app.MapGet("/api/staff/candidates", async (CallsignDbContext db, OperationsService ops, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            // Never offer someone already on your roster, and rotate the slate roughly weekly so the market has
            // fresh faces over time (not the same five forever). Seed stays deterministic within a window.
            var onStaff = await db.Staff.Where(s => s.CompanyId == pilot.CompanyId && s.IsActive && !s.IsDeleted).Select(s => s.Name).ToListAsync();
            int week = (int)((clock.UtcNow - DateTimeOffset.UnixEpoch).TotalDays / 7);
            int seed = pilot.CompanyId.GetHashCode() ^ unchecked(week * (int)0x9E3779B1);
            return Results.Ok(ops.GenerateCandidates(seed, 6, onStaff)
                .Select(c => new StaffCandidateDto(c.Seed, c.Name, c.WagePerDayCents, c.SkillMilli)));
        });

        app.MapGet("/api/staff", async (CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var staff = await db.Staff.Where(s => s.CompanyId == pilot.CompanyId && s.IsActive && !s.IsDeleted).ToListAsync();
            return Results.Ok(staff.Select(s => new StaffDto(s.Id, s.Name, s.WagePerDayCents, s.SkillMilli)));
        });

        app.MapPost("/api/staff/hire", async (HireRequest req, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var s = await ops.HireAsync(pilot.CompanyId, req.CandidateSeed);
            return Results.Ok(new { id = s.Id, name = s.Name });
        });

        app.MapGet("/api/ops/orders", async (CallsignDbContext db, EconomyConfig cfg, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var orders = await db.StandingOrders.Where(o => o.CompanyId == pilot.CompanyId && o.IsActive && !o.IsDeleted).ToListAsync();
            var staffNames = await db.Staff.Where(s => s.CompanyId == pilot.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name);
            var tails = await db.AircraftInstances.Where(a => a.CompanyId == pilot.CompanyId).ToDictionaryAsync(a => a.Id, a => a.Tail);
            var now = clock.UtcNow;
            return Results.Ok(orders.Select(o =>
            {
                long effReward = (long)Math.Round(o.RewardPerTripCents * (o.PriceMultiplierMilli / 1000.0));
                int fillPct = (int)Math.Round(cfg.ContractFillProbability(o.PriceMultiplierMilli) * 100);
                int pending = cfg.AutonomousTripsFlown((now - o.LastReconciledAt).TotalHours, o.RoundTripHours); // trips ready to bank
                return new StandingOrderDto(o.Id,
                    staffNames.GetValueOrDefault(o.StaffId, "?"), tails.GetValueOrDefault(o.AircraftInstanceId, "?"),
                    o.OriginIcao, o.DestIcao, o.DistanceNm, o.RoundTripHours, effReward,
                    o.PriceMultiplierMilli, fillPct, o.RewardPerTripCents, pending, pending * effReward);
            }));
        });

        app.MapPost("/api/ops/orders", async (StandingOrderRequest req, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var o = await ops.CreateStandingOrderAsync(pilot.CompanyId, req.StaffId, req.AircraftInstanceId, req.DestIcao, req.PriceMultiplierMilli ?? 1000);
                return Results.Ok(new { id = o.Id, roundTripHours = o.RoundTripHours, rewardPerTripCents = o.RewardPerTripCents, priceMultiplierMilli = o.PriceMultiplierMilli });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/ops/orders/{id:guid}/price", async (Guid id, OrderPriceRequest req, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                int markup = await ops.SetOrderPriceAsync(pilot.CompanyId, id, req.PriceMultiplierMilli);
                return Results.Ok(new { id, priceMultiplierMilli = markup });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/ops/orders/{id:guid}/cancel", async (Guid id, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            await ops.CancelStandingOrderAsync(pilot.CompanyId, id);
            return Results.Ok(new { cancelled = id });
        });

        app.MapPost("/api/ops/reconcile", async (CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var d = await ops.ReconcileAsync(pilot.CompanyId);
            return Results.Ok(new ReconcileDto(d.Trips, d.GrossIncomeCents, d.FeesCents, d.WagesCents, d.RentCents, d.LoanCents, d.InsuranceCents, d.NetCents, d.Incidents, d.Grounded, d.DutyMaxed, d.EmptyLegs, d.LoanWarnings ?? [], d.Defaults ?? [], d.CertLapsed ?? [], d.WeatheredOut, d.CertExpiring ?? [], d.RentalCents, d.RentalsExpiring ?? [], d.RentalsAutoReturned ?? [], d.OperatingRepDeltaMilli));
        });

        // --- Loans (Phase 4a) ---
        app.MapGet("/api/loans", async (CallsignDbContext db, LoanService loans) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var active = await loans.GetActiveAsync(pilot.CompanyId);
            var (credit, offers) = await loans.OffersForAsync(pilot.CompanyId);
            return Results.Ok(new
            {
                loans = active.Select(l => new LoanDto(l.Id, l.Tier, l.PrincipalCents, l.OutstandingCents,
                    l.AprBps, l.TermDays, l.Status.ToString(), l.TakenAt)),
                offers = offers.Select(o => new LoanOfferDto(o.Tier.Tier, o.Tier.Name, o.Tier.MinPrincipalCents, o.Tier.MaxPrincipalCents, o.Tier.AprBps, o.EffectiveAprBps)),
                credit = new CreditDto(credit.Score, credit.Grade, credit.AprDeltaBps),
            });
        });

        app.MapPost("/api/loans/take", async (TakeLoanRequest req, CallsignDbContext db, LoanService loans) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var l = await loans.TakeAsync(pilot.CompanyId, req.PrincipalCents);
                return Results.Ok(new { id = l.Id, outstandingCents = l.OutstandingCents, aprBps = l.AprBps });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/loans/{id:guid}/payoff", async (Guid id, CallsignDbContext db, LoanService loans) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var paid = await loans.PayoffAsync(pilot.CompanyId, id);
                return Results.Ok(new { paidCents = paid });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // The balance sheet (Phase 4b): a computed net worth + a cash-flow / P&L window. No money moves.
        app.MapGet("/api/finances", async (int? days, CallsignDbContext db, FinanceService finance) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var nw = await finance.NetWorthAsync(pilot.CompanyId);
            var pnl = await finance.ProfitLossAsync(pilot.CompanyId, days ?? 30);
            return Results.Ok(new
            {
                netWorth = new NetWorthDto(nw.CashCents, nw.AircraftCents, nw.InventoryCents, nw.LoansCents, nw.NetWorthCents),
                pnl = new PnlDto(pnl.Days, pnl.IncomeCents, pnl.ExpenseCents, pnl.NetCents,
                    pnl.Lines.Select(l => new PnlLineDto(l.Category, l.IncomeCents, l.ExpenseCents, l.NetCents)).ToList()),
            });
        });

        // Deep finances: per-aircraft/staff/base P&L (the ledger attribution columns rolled up) + a
        // reconstructed cash-flow curve. Read-only.
        app.MapGet("/api/finances/detail", async (int? days, CallsignDbContext db, FinanceService finance) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            int d = days ?? 30;
            var attr = await finance.AttributionProfitLossAsync(pilot.CompanyId, d);
            var series = await finance.CashFlowSeriesAsync(pilot.CompanyId, d);
            static AttributionLineDto Map(AttributionLine l) =>
                new(l.Kind, l.Id, l.Label, l.Sub, l.IncomeCents, l.ExpenseCents, l.NetCents, l.Entries);
            return Results.Ok(new FinanceDetailDto(d,
                attr.Aircraft.Select(Map).ToList(),
                attr.Staff.Select(Map).ToList(),
                attr.Bases.Select(Map).ToList(),
                series.Points.Select(p => new CashPointDto(p.At, p.IncomeCents, p.ExpenseCents, p.NetCents, p.CashCents)).ToList()));
        });

        // The itemised statement for the window (attribution resolved) — powers the statement table + CSV export.
        app.MapGet("/api/finances/statement", async (int? days, CallsignDbContext db, FinanceService finance) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var rows = await finance.StatementAsync(pilot.CompanyId, days ?? 30);
            return Results.Ok(rows.Select(r =>
                new StatementRowDto(r.At, r.Category, r.AmountCents, r.Description, r.Aircraft, r.Staff, r.Base)));
        });

        // --- Insurance (Phase 4c) ---
        app.MapGet("/api/insurance", async (CallsignDbContext db, InsuranceService ins, AircraftDealerService dealer, EconomyConfig cfg) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var policies = await ins.GetActiveAsync(pilot.CompanyId);
            var insured = policies.Where(p => p.AircraftInstanceId is not null).Select(p => p.AircraftInstanceId!.Value).ToHashSet();
            var hangar = await dealer.GetHangarAsync(pilot.CompanyId);
            var byId = hangar.ToDictionary(h => h.Instance.Id);

            var policyDtos = policies.Select(p =>
            {
                byId.TryGetValue(p.AircraftInstanceId ?? Guid.Empty, out var h);
                int cond = h is null ? 0 : Math.Min(h.Instance.HullConditionMilli, h.Instance.EngineConditionMilli);
                return new InsurancePolicyDto(p.Id, h?.Instance.Tail ?? "—", h?.Type.CanonicalName ?? "—", cond, p.CoverageMilli,
                    p.PremiumPerWeekCents, p.DeductibleCents, p.ClaimPayoutCents, cond <= cfg.InsuranceTotalLossConditionMilli);
            }).ToList();

            var quotes = new List<InsuranceQuoteDto>();
            foreach (var h in hangar.Where(h => !insured.Contains(h.Instance.Id)))
            {
                var q = await ins.QuoteAsync(pilot.CompanyId, h.Instance.Id, null);
                if (q is not null)
                    quotes.Add(new InsuranceQuoteDto(h.Instance.Id, h.Instance.Tail, h.Type.CanonicalName,
                        q.PremiumPerWeekCents, q.DeductibleCents, q.ClaimPayoutCents));
            }
            return Results.Ok(new { policies = policyDtos, quotes });
        });

        app.MapPost("/api/insurance/insure", async (InsureRequest req, CallsignDbContext db, InsuranceService ins) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var p = await ins.InsureAsync(pilot.CompanyId, req.AircraftInstanceId, req.CoverageMilli);
                return Results.Ok(new { id = p.Id, premiumPerWeekCents = p.PremiumPerWeekCents });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/insurance/{id:guid}/cancel", async (Guid id, CallsignDbContext db, InsuranceService ins) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            await ins.CancelAsync(pilot.CompanyId, id);
            return Results.Ok(new { cancelled = id });
        });

        app.MapPost("/api/insurance/{id:guid}/claim", async (Guid id, CallsignDbContext db, InsuranceService ins) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var paid = await ins.ClaimAsync(pilot.CompanyId, id);
                return Results.Ok(new { paidCents = paid });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Routes (Phase 4d) ---
        app.MapGet("/api/routes", async (CallsignDbContext db, RouteService routes, BaseService bases, EconomyConfig cfg, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var active = await routes.GetRoutesAsync(pilot.CompanyId);
            var baseViews = await bases.GetBasesAsync(pilot.CompanyId);
            var routeStaff = await db.Staff.Where(s => s.CompanyId == pilot.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name);
            var routeTails = await db.AircraftInstances.Where(a => a.CompanyId == pilot.CompanyId).ToDictionaryAsync(a => a.Id, a => a.Tail);
            var nowR = clock.UtcNow;
            var missions = MissionCatalog.All.Where(d => d.MinReputationMilli == 0 && d.ReputationMilliReward >= 0)
                .Select(d => d.Type.ToString()).ToList();
            // Phase 11f — whether we hold a valid Air Operator Certificate, so the UI can offer scheduled service.
            bool hasAoc = await db.OperatingCertificates.AnyAsync(c => c.CompanyId == pilot.CompanyId && c.Kind == CertificateKind.AirOperator && c.ExpiresAt > clock.UtcNow);
            return Results.Ok(new
            {
                routes = active.Select(r =>
                {
                    long effReward = (long)Math.Round(r.RewardPerTripCents * (r.PriceMultiplierMilli / 1000.0));
                    int fillPct = (int)Math.Round(cfg.ContractFillProbability(r.PriceMultiplierMilli) * 100);
                    int pending = cfg.AutonomousTripsFlown((nowR - r.LastReconciledAt).TotalHours, r.RoundTripHours); // trips ready to bank
                    return new RouteDto(r.Id, r.Name, r.OriginIcao, r.DestIcao, r.Mission.ToString(),
                        r.DistanceNm, r.RoundTripHours, effReward, r.PriceMultiplierMilli, fillPct, r.RewardPerTripCents,
                        r.SeatCapacity, r.LoadFactorMilli,
                        routeStaff.GetValueOrDefault(r.StaffId, "?"), routeTails.GetValueOrDefault(r.AircraftInstanceId, "?"),
                        pending, pending * effReward);
                }),
                bases = baseViews.Select(b => new RouteBaseDto(b.Icao, b.Name)),
                missions,
                hasAoc,
            });
        });

        app.MapPost("/api/routes", async (CreateRouteRequest req, CallsignDbContext db, RouteService routes) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            if (!Enum.TryParse<MissionType>(req.Mission, ignoreCase: true, out var mission))
                return Results.BadRequest(new { error = $"Unknown mission '{req.Mission}'." });
            try
            {
                var r = await routes.CreateRouteAsync(pilot.CompanyId, req.Name, req.OriginIcao, req.DestIcao, req.AircraftInstanceId, req.StaffId, mission, req.PriceMultiplierMilli ?? 1000);
                return Results.Ok(new { id = r.Id, name = r.Name, rewardPerTripCents = r.RewardPerTripCents, priceMultiplierMilli = r.PriceMultiplierMilli });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Phase 11f — open a scheduled-passenger route (AOC-gated; seats × load × yield frozen at creation).
        app.MapPost("/api/routes/scheduled", async (ScheduledRouteRequest req, CallsignDbContext db, RouteService routes) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var r = await routes.CreateScheduledServiceAsync(pilot.CompanyId, req.Name, req.OriginIcao, req.DestIcao, req.AircraftInstanceId, req.StaffId);
                return Results.Ok(new { id = r.Id, name = r.Name, rewardPerTripCents = r.RewardPerTripCents, seatCapacity = r.SeatCapacity, loadFactorMilli = r.LoadFactorMilli });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/routes/{id:guid}/price", async (Guid id, OrderPriceRequest req, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                int markup = await ops.SetRoutePriceAsync(pilot.CompanyId, id, req.PriceMultiplierMilli);
                return Results.Ok(new { id, priceMultiplierMilli = markup });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/routes/{id:guid}/cancel", async (Guid id, CallsignDbContext db, RouteService routes) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            await routes.CancelRouteAsync(pilot.CompanyId, id);
            return Results.Ok(new { cancelled = id });
        });

        // --- Bases (Phase 2e) ---
        app.MapGet("/api/bases", async (CallsignDbContext db, BaseService bases, EconomyConfig cfg) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var list = await bases.GetBasesAsync(pilot.CompanyId);
            return Results.Ok(list.Select(b => new BaseViewDto(b.Id, b.Icao, b.Name, b.IsHome, b.RentPerDayCents, b.Latitude, b.Longitude,
                b.MaintenanceLevel,
                b.MaintenanceLevel < cfg.MaxMaintenanceShopLevel ? cfg.MaintenanceShopUpgradeCents(b.MaintenanceLevel + 1) : 0,
                cfg.MaintenanceShopDiscountPct(b.MaintenanceLevel),
                b.FuelFarmLevel,
                b.FuelFarmLevel < cfg.MaxFuelFarmLevel ? cfg.FuelFarmUpgradeCents(b.FuelFarmLevel + 1) : 0,
                cfg.FuelFarmDiscountPct(b.FuelFarmLevel),
                b.HubLevel,
                b.HubLevel < cfg.MaxHubLevel ? cfg.HubUpgradeCents(b.HubLevel + 1) : 0,
                1.0 + b.HubLevel * cfg.HubLevelAmplification))); // the demand-lift amplification at this hub level (Phase 11e)
        });

        app.MapPost("/api/bases/{id:guid}/upgrade-shop", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { maintenanceLevel = await bases.UpgradeMaintenanceShopAsync(pilot.CompanyId, id, idem) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/bases/{id:guid}/upgrade-fuel-farm", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { fuelFarmLevel = await bases.UpgradeFuelFarmAsync(pilot.CompanyId, id, idem) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/bases/{id:guid}/upgrade-hub", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try { return Results.Ok(new { hubLevel = await bases.UpgradeHubAsync(pilot.CompanyId, id, idem) }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/bases/candidates", async (CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var offers = await bases.GetCandidatesAsync(pilot.CompanyId);
            return Results.Ok(offers.Select(o => new BaseOfferDto(o.Icao, o.Name, o.Kind, o.DistanceNm, o.OpenCents, o.RentPerDayCents, o.Latitude, o.Longitude)));
        });

        app.MapPost("/api/bases/open", async (OpenBaseRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var b = await bases.OpenBaseAsync(pilot.CompanyId, req.AirportIcao, idem);
                return Results.Ok(new { id = b.Id, icao = b.AirportIcao });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- World calendar (Phase 8): the date, season and how long the company has been operating ---
        app.MapGet("/api/world", async (CallsignDbContext db, Callsign.Core.World.WorldOracle world, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var airport = await db.Airports.FirstOrDefaultAsync(a => a.Ident == pilot.CurrentIcao);
            var now = clock.UtcNow;
            var founded = await db.LedgerEntries.Where(e => e.AccountId == pilot.CompanyId)
                .MinAsync(e => (DateTimeOffset?)e.At) ?? now; // the first posting (starting balance) marks the career start
            var econ = world.EconomyPhaseAt(now);
            return Results.Ok(new WorldStateDto(
                now.UtcDateTime.ToString("yyyy-MM-dd"), now.UtcDateTime.DayOfWeek.ToString(),
                Callsign.Core.World.GameCalendar.Season(now, airport?.Latitude ?? 0),
                Callsign.Core.World.GameCalendar.CareerDays(founded, now),
                econ.Label, (int)Math.Round((econ.DemandMult - 1) * 100)));
        });

        // --- Weather (Phase 8): the synthetic world model at a field (current airport, or ?icao=) ---
        app.MapGet("/api/weather", async (string? icao, CallsignDbContext db, Callsign.Core.World.WeatherProvider weather, IClock clock) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var ident = string.IsNullOrWhiteSpace(icao) ? pilot.CurrentIcao : icao;
            var airport = await db.Airports.FirstOrDefaultAsync(a => a.Ident == ident);
            if (airport is null) return Results.NotFound();
            // Phase 9b: the freshest live METAR if the seam has one, else the synthetic model (byte-identical to
            // Phase 8 when live weather is off). Never blocks — a dead feed just yields the synthetic value.
            var r = weather.Observed(airport.Latitude, airport.Longitude, clock.UtcNow, airport.Ident);
            var w = r.Weather;
            return Results.Ok(new WeatherDto(airport.Ident, airport.Name, w.WindDirDeg, w.WindKts, w.GustKts,
                w.VisibilitySm, w.CeilingFt, w.TempC, w.Condition, w.Summary,
                r.IsLive, r.IsLive ? r.AsOf.ToString("o") : null, r.IsLive ? airport.Ident : null));
        });

        // --- Clients (Phase 8d-2): your named relationships and where each stands now (loyalty decayed) ---
        app.MapGet("/api/clients", async (CallsignDbContext db, ClientService clients) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            return Results.Ok(await clients.GetClientsAsync(pilot.CompanyId));
        });

        // --- Operating certificates (Phase 8e): earn/renew the licences that gate premium categories ---
        app.MapGet("/api/certificates", async (CallsignDbContext db, CertificateService certs) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            return Results.Ok(await certs.GetStatusAsync(pilot.CompanyId));
        });

        app.MapPost("/api/certificates/{kind}/apply", async (string kind, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, CertificateService certs) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            if (!Enum.TryParse<CertificateKind>(kind, ignoreCase: true, out var k))
                return Results.BadRequest(new { error = $"Unknown certificate '{kind}'." });
            try
            {
                var cert = await certs.ApplyAsync(pilot.CompanyId, k, idem);
                return Results.Ok(new { kind = cert.Kind.ToString(), issuedAt = cert.IssuedAt, expiresAt = cert.ExpiresAt });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Trade (Phase 2g): the market is priced at your current airport ---
        app.MapGet("/api/trade/market", async (CallsignDbContext db, TradeService trade) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var quotes = await trade.GetMarketAsync(pilot.CompanyId, pilot.CurrentIcao);
            return Results.Ok(quotes
                .Select(q => new MarketQuoteDto(q.Good, q.Name, q.BuyCents, q.SellCents, q.UnitWeightLbs, q.Region, q.PressurePct, q.WeatherPct)));
        });

        app.MapGet("/api/trade/inventory", async (CallsignDbContext db, TradeService trade) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var inv = await trade.GetInventoryAsync(pilot.CompanyId);
            return Results.Ok(inv.Select(v => new InventoryDto(v.Id, v.Good, v.Name, v.Quantity, v.UnitCostCents,
                v.MarketSellCents, v.UnrealizedPnlCents, v.UnitWeightLbs, v.LocationIcao)));
        });

        app.MapPost("/api/trade/buy", async (TradeRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, TradeService trade) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var lot = await trade.BuyAsync(pilot.CompanyId, pilot.CurrentIcao, req.Good, req.Qty, idem);
                return Results.Ok(new { id = lot.Id, good = lot.Good, quantity = lot.Quantity, unitCostCents = lot.UnitCostCents });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/trade/sell", async (TradeRequest req, [FromHeader(Name = "Idempotency-Key")] string? idem, CallsignDbContext db, TradeService trade) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var r = await trade.SellAsync(pilot.CompanyId, pilot.CurrentIcao, req.Good, req.Qty, idem);
                return Results.Ok(new TradeResultDto(r.Quantity, r.ProceedsCents, r.CostBasisCents, r.PnlCents));
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Cash changed at the same time — try again." }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/ledger", async (int? limit, CallsignDbContext db) =>
        {
            var entries = await db.LedgerEntries.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync();
            return Results.Ok(entries.Select(e => new LedgerDto(e.At, e.Category.ToString(), e.AmountCents, e.Description)));
        });

        // The logbook feed (Phase 6): widened, joined flight rows. Paged via ?skip&take; the frozen
        // JobAssignment supplies dep/arr ICAO + mission, Airports supply coords + name, the owned airframe
        // supplies the tail. Newest-first.
        app.MapGet("/api/flights", async (int? skip, int? take, CallsignDbContext db) =>
        {
            var n = Math.Clamp(take ?? 50, 1, 200);
            var s = Math.Max(0, skip ?? 0);
            var flights = await db.Flights.OrderByDescending(f => f.SettledAt).Skip(s).Take(n).ToListAsync();

            var asgIds = flights.Where(f => f.JobAssignmentId is not null).Select(f => f.JobAssignmentId!.Value).Distinct().ToList();
            var asgs = await db.JobAssignments.Where(a => asgIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a);
            var instIds = flights.Where(f => f.AircraftInstanceId is not null).Select(f => f.AircraftInstanceId!.Value).Distinct().ToList();
            var tails = await db.AircraftInstances.Where(a => instIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Tail);
            var ports = await AirportsByIdentAsync(db, asgs.Values.SelectMany(a => new[] { a.OriginIcao, a.DestIcao }));

            return Results.Ok(flights.Select(f =>
            {
                JobAssignment? a = f.JobAssignmentId is { } id && asgs.TryGetValue(id, out var av) ? av : null;
                Callsign.Core.Domain.Airport? o = a is not null && ports.TryGetValue(a.OriginIcao, out var ov) ? ov : null;
                Callsign.Core.Domain.Airport? d = a is not null && ports.TryGetValue(a.DestIcao, out var dv) ? dv : null;
                double dur = Math.Max(0, (f.ArrivedAt - f.DepartedAt).TotalHours);
                return new FlightDto(
                    f.Id, f.AircraftTitle, f.AircraftTypeId,
                    f.AircraftInstanceId is { } iid && tails.TryGetValue(iid, out var t) ? t : null,
                    a?.Type.ToString(),
                    a?.OriginIcao ?? "—", a?.DestIcao ?? "—", d?.Name ?? a?.DestIcao ?? "—",
                    f.DistanceNm, f.FuelUsedLbs, dur, f.TouchdownFpm, f.PayoutCents, f.Xp,
                    f.DepartedAt, f.ArrivedAt, f.SettledAt,
                    o?.Latitude ?? 0, o?.Longitude ?? 0, d?.Latitude ?? 0, d?.Longitude ?? 0,
                    f.OverallScore, f.ScoreValid);
            }));
        });

        // Lifetime totals over EVERY flight — the logbook footer stays true past the paged window.
        app.MapGet("/api/flights/totals", async (CallsignDbContext db) =>
        {
            var f = await db.Flights
                .Select(x => new { x.DepartedAt, x.ArrivedAt, x.DistanceNm, x.FuelUsedLbs, x.TouchdownFpm, x.PayoutCents, x.Xp, x.OverallScore, x.ScoreValid })
                .ToListAsync();
            if (f.Count == 0)
                return Results.Ok(new FlightTotalsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            double hours = f.Sum(x => Math.Max(0, (x.ArrivedAt - x.DepartedAt).TotalHours));
            // Lifetime flying QUALITY over scored, valid flights (the un-gameable measure — a cheated leg doesn't count).
            var scored = f.Where(x => x.OverallScore.HasValue && (x.ScoreValid ?? true)).Select(x => x.OverallScore!.Value).ToList();
            return Results.Ok(new FlightTotalsDto(
                f.Count, hours, f.Sum(x => x.DistanceNm), f.Sum(x => x.FuelUsedLbs),
                f.Sum(x => x.PayoutCents), f.Sum(x => x.Xp),
                f.Average(x => x.TouchdownFpm), f.Min(x => Math.Abs(x.TouchdownFpm)),
                f.Max(x => x.PayoutCents), f.Max(x => x.DistanceNm),
                scored.Count, scored.Count > 0 ? scored.Average() : 0, scored.Count > 0 ? scored.Max() : 0));
        });

        // One flight in full, incl. the itemised payout from the frozen breakdown JSON. The literal
        // "/totals" route above always wins over this {id:guid}, so ordering is safe.
        app.MapGet("/api/flights/{id:guid}", async (Guid id, CallsignDbContext db) =>
        {
            var f = await db.Flights.FirstOrDefaultAsync(x => x.Id == id);
            if (f is null) return Results.NotFound();

            JobAssignment? a = f.JobAssignmentId is { } aid
                ? await db.JobAssignments.FirstOrDefaultAsync(x => x.Id == aid) : null;
            var ports = a is null
                ? new Dictionary<string, Callsign.Core.Domain.Airport>()
                : await AirportsByIdentAsync(db, new[] { a.OriginIcao, a.DestIcao });
            Callsign.Core.Domain.Airport? o = a is not null && ports.TryGetValue(a.OriginIcao, out var ov) ? ov : null;
            Callsign.Core.Domain.Airport? d = a is not null && ports.TryGetValue(a.DestIcao, out var dv) ? dv : null;

            string? tail = f.AircraftInstanceId is { } iid
                ? (await db.AircraftInstances.FirstOrDefaultAsync(x => x.Id == iid))?.Tail : null;
            string? acName = null, acCat = null;
            if (f.AircraftTypeId is { } tid)
            {
                var ty = await db.AircraftTypes.FirstOrDefaultAsync(x => x.Id == tid);
                acName = ty?.CanonicalName; acCat = ty?.Category.ToString();
            }

            var lines = new List<PayoutLineDto>();
            try
            {
                var bd = JsonSerializer.Deserialize<Callsign.Core.Economy.PayoutBreakdown>(f.PayoutBreakdownJson);
                if (bd is not null)
                    lines = bd.Lines.Select(l => new PayoutLineDto(l.Label, l.AmountCents)).ToList();
            }
            catch { /* tolerate a legacy/empty breakdown — the row still renders without lines */ }

            var events = await db.FlightEvents.Where(e => e.FlightId == f.Id).OrderBy(e => e.Seq)
                .Select(e => new FlightEventDto(e.At, e.Severity, e.Message)).ToListAsync();

            // Phase 10a — distil the scored flight + its recorded events into a coaching debrief (pure; no economic effect).
            var report = Callsign.Core.Debrief.FlightDebrief.Build(new Callsign.Core.Debrief.DebriefInput(
                f.OverallScore.HasValue, f.ScoreValid ?? true, f.OverallScore ?? 0, f.LandingScore ?? 0, f.ApproachScore ?? 0,
                f.TouchdownFpmWorst3 ?? f.TouchdownFpm, f.TouchdownG ?? 1.0, f.StabilizedApproach ?? false, f.ViolationPoints ?? 0,
                f.OutcomeGrade, f.OutcomeReason,
                events.Select(e => new Callsign.Core.Debrief.DebriefEvent(e.Severity, e.Message)).ToList()));
            static DebriefNoteDto Note(Callsign.Core.Debrief.DebriefNote n) => new(n.Tone.ToString(), n.Dimension, n.Headline, n.Detail);
            var debrief = new DebriefDto(report.Scored, report.ScoreValid, report.OverallScore, report.Grade, report.Headline,
                report.Strengths.Select(Note).ToList(), report.ToImprove.Select(Note).ToList());

            double dur = Math.Max(0, (f.ArrivedAt - f.DepartedAt).TotalHours);
            return Results.Ok(new FlightDetailDto(
                f.Id, f.AircraftTitle, f.AircraftTypeId, tail, acName, acCat,
                a?.Type.ToString(), a?.OriginIcao ?? "—", o?.Name ?? a?.OriginIcao ?? "—",
                a?.DestIcao ?? "—", d?.Name ?? a?.DestIcao ?? "—",
                f.DistanceNm, f.FuelUsedLbs, dur, f.TouchdownFpm, f.PayoutCents, f.Xp,
                f.DepartedAt, f.ArrivedAt, f.SettledAt,
                o?.Latitude ?? 0, o?.Longitude ?? 0, d?.Latitude ?? 0, d?.Longitude ?? 0,
                a?.Pax, a?.WeightLbs, a?.Commodity, lines, events,
                f.OverallScore, f.LandingScore, f.ApproachScore, f.ComfortScore, f.TouchdownG, f.StabilizedApproach, f.ViolationPoints, f.ScoreValid,
                f.OutcomeGrade, f.OutcomeReason, debrief));
        });

        // --- Live flight: begin tracking an accepted assignment; the next landing auto-settles it ---
        app.MapPost("/api/flight/begin", async (BeginFlightRequest req, CallsignDbContext db, QualificationService quals, AircraftDealerService dealer, FlightSessionService session) =>
        {
            // Rating gate (Phase 3c): dispatching an OWNED airframe needs the licence class for its category.
            if (req.AircraftInstanceId is { } aid)
            {
                var pilot = await db.Pilots.FirstOrDefaultAsync();
                var inst = await db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aid);
                var type = inst is null ? null : await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId);
                if (pilot is not null && type is not null)
                {
                    var required = QualificationClasses.ForCategory(type.Category);
                    if (!await quals.IsRatedAsync(pilot.Id, required))
                        return Results.BadRequest(new { error = $"You're not rated for the {type.CanonicalName} — it needs {QualificationClasses.Def(required).DisplayName}." });
                }
                // Airworthiness gate (Phase 7e): a grounded tail — worn out or overdue an inspection — can't fly.
                if (inst is not null && dealer.Airworthiness(inst) is { Airworthy: false } aw)
                    return Results.BadRequest(new { error = $"{inst.Tail} is grounded: {aw.Reason}." });
            }
            session.BeginFlight(req.AssignmentId, req.AircraftInstanceId);
            return Results.Ok(new { begun = req.AssignmentId, aircraft = req.AircraftInstanceId });
        });

        // Licence classes (Phase 3c/3d): the full ladder, flagged with what the pilot holds + the
        // check-flight fee to earn each — self-documenting.
        app.MapGet("/api/quals", async (CallsignDbContext db, QualificationService quals, EconomyConfig cfg) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            var held = pilot is null
                ? new List<Callsign.Core.Domain.PilotQualification>()
                : await quals.GetHeldAsync(pilot.Id);
            var stars = held.ToDictionary(q => q.Class, q => q.Stars);
            return Results.Ok(QualificationClasses.All.Select(c => new QualClassDto(
                c.Class.ToString(), c.DisplayName, c.Description, stars.ContainsKey(c.Class),
                stars.GetValueOrDefault(c.Class), cfg.CheckFlightFeeCents(c.Class))));
        });

        // Begin a check-flight (Phase 3d): the next landing is graded and, on a pass, earns the class.
        app.MapPost("/api/checkflights/begin", async (CheckFlightBeginRequest req, CallsignDbContext db, FlightSessionService session) =>
        {
            if (!Enum.TryParse<QualClass>(req.Class, ignoreCase: true, out var cls))
                return Results.BadRequest(new { error = $"Unknown class '{req.Class}'." });
            if (await db.Pilots.FirstOrDefaultAsync() is null)
                return Results.NotFound();
            session.BeginCheckFlight(cls);
            return Results.Ok(new { begun = cls.ToString() });
        });

        // Grade a submitted check-flight result directly (used by tests / a manual submit path).
        app.MapPost("/api/checkflights/attempt", async (CheckFlightAttemptRequest req, CallsignDbContext db, CheckFlightService check) =>
        {
            if (!Enum.TryParse<QualClass>(req.Class, ignoreCase: true, out var cls))
                return Results.BadRequest(new { error = $"Unknown class '{req.Class}'." });
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var d = req.Flight;
            var record = new Callsign.Core.Flight.FlightRecord(
                d.AircraftTitle, d.DepartedAt, d.ArrivedAt, d.TouchdownFpm, d.MaxAltitudeFt,
                d.DepartureLat, d.DepartureLon, d.ArrivalLat, d.ArrivalLon, d.DistanceNm, d.FuelUsedLbs, []);
            try
            {
                var r = await check.AttemptAsync(pilot.CompanyId, pilot.Id, cls, record);
                return Results.Ok(new CheckFlightResultDto(r.Passed, r.Class.ToString(),
                    QualificationClasses.Def(r.Class).DisplayName, r.Stars, r.FeeCents, r.TouchdownFpm));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/flight/live", (FlightSessionService session) =>
        {
            var t = session.Latest;
            return Results.Ok(new FlightLiveDto(
                session.Phase.ToString(), session.Connection.ToString(), session.CurrentAssignmentId,
                t?.AltitudeFt, t?.IndicatedAirspeedKts, t?.VerticalSpeedFpm, t?.OnGround, t?.AircraftTitle));
        });

        // WebSocket that pushes live telemetry + settlement events to the UI.
        app.Map("/ws/telemetry", async (HttpContext ctx, FlightSessionService session) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await session.AddClientAsync(ws, ctx.RequestAborted);
        });

        // --- Achievements (Phase 5a): evaluating on read awards any newly-earned badges, then returns all ---
        app.MapGet("/api/achievements", async (CallsignDbContext db, AchievementService ach) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var views = await ach.EvaluateAsync(pilot.CompanyId, pilot.Id);
            return Results.Ok(views.Select(v => new AchievementDto(
                v.Key, v.Name, v.Description, v.Category, v.Target, v.Progress, v.Earned, v.EarnedAt)));
        });

        // --- Campaigns (Phase 5b): evaluating on read advances arcs + pays completion rewards, then returns all ---
        app.MapGet("/api/campaigns", async (CallsignDbContext db, CampaignService campaigns) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var views = await campaigns.EvaluateAsync(pilot.CompanyId, pilot.Id);
            return Results.Ok(views.Select(v => new CampaignDto(
                v.Key, v.Name, v.Description, v.RewardCents, v.StepIndex, v.StepCount, v.Completed, v.CompletedAt,
                v.Steps.Select(s => new CampaignStepDto(s.Title, s.Detail, s.Target, s.Progress, s.Done)).ToList())));
        });

        // --- Airline identity + standing (Phase 5c) ---
        app.MapGet("/api/airline", async (CallsignDbContext db, AirlineService airline) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var id = await airline.GetIdentityAsync(pilot.CompanyId);
            var st = await airline.GetStandingAsync(pilot.CompanyId, pilot.Id);
            // Phase 11a — the operating-reputation figure + a two-source ("your flying / your crew") split over the
            // recent log, so the tab can show what's moving the name and coach a fall (never a red penalty).
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == pilot.CompanyId);
            if (company is null) return Results.NotFound(new { error = "Career data is incomplete (no account)." });
            var recent = await db.AirlineReputationEvents
                .Where(e => e.CompanyId == pilot.CompanyId)
                .OrderByDescending(e => e.Id).Take(8).ToListAsync();
            var reputation = new AirlineReputationDto(
                company.OperatingReputationMilli,
                recent.Where(e => e.Source == AirlineRepSource.Player).Sum(e => e.DeltaMilli),
                recent.Where(e => e.Source == AirlineRepSource.Crew).Sum(e => e.DeltaMilli),
                recent.Select(e => new AirlineRepEventDto(e.DeltaMilli, e.BalanceMilli, e.Source.ToString(), e.Reason, e.At)).ToList());
            return Results.Ok(new AirlineDto(
                new AirlineIdentityDto(id.Name, id.TailCode, id.AccentColorHex, id.EmblemKey, id.Customised),
                new AirlineStandingDto(st.Stage, st.StageName, st.Score,
                    st.Contributions.Select(c => new StandingContributionDto(c.Label, c.Points)).ToList(),
                    st.Stages.Select(s => new CareerStageDto(s.Index, s.Name, s.Tagline, s.Reached,
                        s.Requirements.Select(r => new StageRequirementDto(r.Label, r.Metric, r.Current, r.Target, r.Display, r.Met, r.Primary)).ToList(),
                        s.Unlocks.Select(u => new StageUnlockDto(u.Text, u.Live)).ToList())).ToList(),
                    st.NextMove is { } nm ? new NextMoveDto(nm.StageIndex, nm.StageName, nm.MetCount, nm.TotalCount, nm.ProgressPct, nm.Metric, nm.Label, nm.Display) : null),
                reputation,
                AirlineEmblems.All));
        });

        app.MapPost("/api/airline", async (SetAirlineRequest req, CallsignDbContext db, AirlineService airline) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var id = await airline.SetIdentityAsync(pilot.CompanyId, req.Name, req.TailCode, req.AccentColorHex, req.EmblemKey);
                return Results.Ok(new AirlineIdentityDto(id.Name, id.TailCode, id.AccentColorHex, id.EmblemKey, id.Customised));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // --- Build identity (the About line) ---
        app.MapGet("/api/version", () =>
        {
            var asm = typeof(CallsignWebApp).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            return Results.Ok(new { version = info.Split('+')[0], product = "Callsign" }); // drop +buildmetadata
        });

        // --- Save management: back up on demand, list/export snapshots, stage a restore for next launch ---
        app.MapPost("/api/save/backup", async (CallsignDbContext db, SaveService saves) =>
        {
            try
            {
                var info = await saves.BackupAsync(db, DateTime.UtcNow);
                return Results.Ok(new { info.Name, info.SizeBytes, info.CreatedUtc });
            }
            catch (Exception ex) // disk full / locked / permission — report cleanly instead of a raw 500
            {
                return Results.Problem($"Couldn't write the backup: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/api/save/backups", (SaveService saves) =>
            Results.Ok(saves.List().Select(b => new { b.Name, b.SizeBytes, b.CreatedUtc })));

        app.MapGet("/api/save/backups/{name}/download", (string name, SaveService saves) =>
        {
            var path = saves.ResolveBackup(name);
            return path is null ? Results.NotFound() : Results.File(path, "application/octet-stream", name);
        });

        app.MapPost("/api/save/restore", (RestoreRequest req, SaveService saves) =>
        {
            var path = saves.ResolveBackup(req.Name);
            if (path is null) return Results.NotFound(new { error = "No such backup." });
            saves.StageRestore(path); // applied on the next launch, before the DB is opened
            return Results.Ok(new { restart = true });
        });

        // SPA fallback: anything that isn't an API/WebSocket route or a static asset returns index.html.
        if (Directory.Exists(uiPath))
        {
            var indexHtml = Path.Combine(Path.GetFullPath(uiPath), "index.html");
            app.MapFallback((HttpContext ctx) =>
                ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/ws")
                    ? Results.NotFound()
                    : Results.File(indexHtml, "text/html"));
        }
    }
}
