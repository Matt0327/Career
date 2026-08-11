using Callsign.Core.Aircraft;
using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Callsign.Core.Time;
using Callsign.Host;
using Callsign.SimConnect;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database: one SQLite file (path overridable via config "Db:Path" / env Db__Path) ---
var dbPath = builder.Configuration["Db:Path"]
             ?? Path.Combine(builder.Environment.ContentRootPath, "callsign.db");
builder.Services.AddDbContext<CallsignDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

// --- Singletons ---
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(EconomyConfig.Default);
builder.Services.AddSingleton<IJobSource>(sp => new CargoJobSource(sp.GetRequiredService<EconomyConfig>()));
builder.Services.AddSingleton<AircraftScanner>();
builder.Services.AddSingleton<ISimTelemetrySource>(_ => new FakeTelemetrySource()); // real adapter wired in 1g-b

// --- Scoped services (per request) ---
builder.Services.AddScoped<AirportRepository>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<NewGameService>();
builder.Services.AddScoped<JobAssignmentService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<AircraftRosterService>();
builder.Services.AddScoped<JobBoardService>();
builder.Services.AddScoped<GameSetupService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Synchronous on purpose: a top-level 'await' here makes the entry point async, which breaks
// WebApplicationFactory's host resolver in integration tests.
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<CallsignDbContext>().Database.EnsureCreated();

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
    return Results.Ok(jobs.Select(j => new JobDto(j.Id, j.Type.ToString(), j.OriginIcao, j.DestIcao,
        j.Commodity, j.WeightLbs, j.DistanceNm, j.RewardCents, j.Xp, j.ExpiresAt)));
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
    return Results.Ok(list.Select(a => new AssignmentDto(a.Id, a.OriginIcao, a.DestIcao, a.Commodity,
        a.WeightLbs, a.DistanceNm, a.RewardQuoteCents, a.XpQuote, a.Status.ToString())));
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

app.Run();

public partial class Program { }
