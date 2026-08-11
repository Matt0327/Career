using Callsign.Core.Aircraft;
using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Callsign.Core.Time;
using Callsign.SimConnect;
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
        var builder = WebApplication.CreateBuilder(args);

        // --- Database: one SQLite file (path overridable via config "Db:Path" / env Db__Path) ---
        var dbPath = builder.Configuration["Db:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "callsign.db");
        builder.Services.AddDbContext<CallsignDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

        // --- Web UI: the Vite build output (path overridable via config "Ui:Path" / env Ui__Path) ---
        var uiPath = builder.Configuration["Ui:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");

        // --- Singletons ---
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(EconomyConfig.Default);
        builder.Services.AddSingleton<IJobSource>(sp => new CargoJobSource(sp.GetRequiredService<EconomyConfig>()));
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
        builder.Services.AddScoped<GameSetupService>();

        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        app.UseWebSockets();

        // --- Serve the built React UI (if present). API + WebSocket routes are matched first;
        //     any other path falls back to index.html so client-side navigation works. ---
        if (Directory.Exists(uiPath))
        {
            var ui = new PhysicalFileProvider(Path.GetFullPath(uiPath));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });
        }

        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<CallsignDbContext>().Database.EnsureCreated();

        // Start streaming telemetry into the flight session (live SimConnect on the Windows build,
        // synthetic source on the portable build or when SimConnect isn't available).
        _ = app.Services.GetRequiredService<FlightSessionService>().StartAsync();

        MapEndpoints(app, uiPath);
        return app;
    }

    // Look up human airport names for a set of idents (so the UI can show "EHRD · Rotterdam The Hague").
    private static async Task<Dictionary<string, string>> AirportNamesAsync(CallsignDbContext db, IEnumerable<string> idents)
    {
        var set = idents.Distinct().ToList();
        if (set.Count == 0)
            return new Dictionary<string, string>();
        return await db.Airports.Where(a => set.Contains(a.Ident)).ToDictionaryAsync(a => a.Ident, a => a.Name);
    }

    private static void MapEndpoints(WebApplication app, string uiPath)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        app.MapPost("/api/game/new", async (NewCareerRequest req, GameSetupService setup) =>
        {
            var (company, pilot) = await setup.StartNewCareerAsync(
                string.IsNullOrWhiteSpace(req.Name) ? "New Pilot" : req.Name!,
                string.IsNullOrWhiteSpace(req.HomeIcao) ? "EHAM" : req.HomeIcao!,
                req.StartingCash ?? 25_000m);
            return Results.Ok(new { pilotId = pilot.Id, companyId = company.Id, home = pilot.HomeIcao });
        });

        app.MapGet("/api/game/state", async (CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null)
                return Results.NotFound(new { error = "No career. POST /api/game/new first." });
            var company = await db.Companies.FirstAsync(c => c.Id == pilot.CompanyId);
            var flights = await db.Flights.CountAsync();
            return Results.Ok(new StateDto(pilot.Name, pilot.Rank.ToString(), pilot.Xp, pilot.CurrentIcao,
                pilot.HomeIcao, company.CashCents, company.Cash, flights));
        });

        app.MapPost("/api/jobs/refresh", async (string? origin, int? count, CallsignDbContext db, JobBoardService board) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var icao = string.IsNullOrWhiteSpace(origin) ? pilot.CurrentIcao : origin!;
            var n = await board.RefreshAsync(icao, pilot.Rank, count ?? 8, Environment.TickCount);
            return Results.Ok(new { origin = icao, generated = n });
        });

        app.MapGet("/api/jobs", async (string? origin, CallsignDbContext db, JobBoardService board) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var icao = string.IsNullOrWhiteSpace(origin) ? pilot.CurrentIcao : origin!;
            var jobs = await board.GetAvailableAsync(icao);
            var names = await AirportNamesAsync(db, jobs.Select(j => j.DestIcao));
            return Results.Ok(jobs.Select(j => new JobDto(j.Id, j.Type.ToString(), j.OriginIcao, j.DestIcao,
                names.GetValueOrDefault(j.DestIcao, j.DestIcao), j.Commodity, j.WeightLbs, j.DistanceNm, j.RewardCents, j.Xp, j.ExpiresAt)));
        });

        app.MapPost("/api/jobs/{id:guid}/accept", async (Guid id, CallsignDbContext db, JobAssignmentService svc) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var a = await svc.AcceptAsync(id, pilot.CompanyId, pilot.Id);
            return Results.Ok(new { assignmentId = a.Id, dest = a.DestIcao, rewardQuoteCents = a.RewardQuoteCents });
        });

        app.MapGet("/api/assignments", async (CallsignDbContext db) =>
        {
            var list = await db.JobAssignments.Where(a => a.Status == AssignmentStatus.Accepted).ToListAsync();
            var names = await AirportNamesAsync(db, list.Select(a => a.DestIcao));
            return Results.Ok(list.Select(a => new AssignmentDto(a.Id, a.OriginIcao, a.DestIcao,
                names.GetValueOrDefault(a.DestIcao, a.DestIcao), a.Commodity, a.WeightLbs, a.DistanceNm, a.RewardQuoteCents, a.XpQuote, a.Status.ToString())));
        });

        app.MapPost("/api/assignments/{id:guid}/settle", async (Guid id, FlightResultDto dto, SettlementService svc) =>
        {
            var record = new Callsign.Core.Flight.FlightRecord(
                dto.AircraftTitle, dto.DepartedAt, dto.ArrivedAt, dto.TouchdownFpm, dto.MaxAltitudeFt,
                dto.DepartureLat, dto.DepartureLon, dto.ArrivalLat, dto.ArrivalLon, dto.DistanceNm, dto.FuelUsedLbs, []);
            var r = await svc.SettleAsync(id, record);
            return Results.Ok(new SettlementDto(r.FlightId, r.PayoutCents, r.XpAwarded, r.PayloadMatched,
                r.Breakdown.Lines.Select(l => new PayoutLineDto(l.Label, l.AmountCents)).ToList()));
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

        app.MapGet("/api/aircraft", async (CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var hangar = await dealer.GetHangarAsync(pilot.CompanyId);
            return Results.Ok(hangar.Select(h => new OwnedAircraftDto(
                h.Instance.Id, h.Instance.Tail, h.Type.CanonicalName, h.Type.Category.ToString(),
                h.Instance.LocationIcao, h.Instance.Availability.ToString(),
                h.Instance.PurchasePriceCents, h.Instance.AirframeHours)));
        });

        app.MapPost("/api/aircraft/buy", async (BuyAircraftRequest req, CallsignDbContext db, AircraftDealerService dealer) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var inst = await dealer.BuyAsync(pilot.CompanyId, req.TypeId, pilot.CurrentIcao);
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

        app.MapGet("/api/ledger", async (int? limit, CallsignDbContext db) =>
        {
            var entries = await db.LedgerEntries.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync();
            return Results.Ok(entries.Select(e => new LedgerDto(e.At, e.Category.ToString(), e.AmountCents, e.Description)));
        });

        app.MapGet("/api/flights", async (CallsignDbContext db) =>
        {
            var flights = await db.Flights.OrderByDescending(f => f.SettledAt).Take(50).ToListAsync();
            return Results.Ok(flights.Select(f => new FlightDto(f.Id, f.AircraftTitle, f.TouchdownFpm, f.PayoutCents, f.Xp, f.SettledAt)));
        });

        // --- Live flight: begin tracking an accepted assignment; the next landing auto-settles it ---
        app.MapPost("/api/flight/begin", (BeginFlightRequest req, FlightSessionService session) =>
        {
            session.BeginFlight(req.AssignmentId);
            return Results.Ok(new { begun = req.AssignmentId });
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
