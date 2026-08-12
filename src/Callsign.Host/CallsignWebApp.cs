using Callsign.Core.Achievements;
using Callsign.Core.Aircraft;
using Callsign.Core.Airline;
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
        var builder = WebApplication.CreateBuilder(args);

        // --- Database: one SQLite file (path overridable via config "Db:Path" / env Db__Path) ---
        var dbPath = builder.Configuration["Db:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "callsign.db");
        // Apply a restore staged in a previous session BEFORE anything opens the file (the live DB can't
        // be swapped while held open), moving the current save aside rather than destroying it.
        SaveService.ApplyPendingRestore(dbPath);
        builder.Services.AddDbContext<CallsignDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        builder.Services.AddSingleton(new SaveService(dbPath));

        // --- Web UI: the Vite build output (path overridable via config "Ui:Path" / env Ui__Path) ---
        var uiPath = builder.Configuration["Ui:Path"]
                     ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");

        // --- Singletons ---
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(EconomyConfig.Default);
        builder.Services.AddSingleton<IJobSource>(sp =>
        {
            var cfg = sp.GetRequiredService<EconomyConfig>();
            // The board mixes the full mission roster (Phase 3e), each type by its catalogue share.
            var sources = MissionCatalog.Generated
                .Select(d => ((IJobSource)new MissionJobSource(d, cfg), d.BoardShare))
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
        builder.Services.AddScoped<ProgressMetricsService>();
        builder.Services.AddScoped<AchievementService>();
        builder.Services.AddScoped<CampaignService>();
        builder.Services.AddScoped<AirlineService>();
        builder.Services.AddSingleton<MarketService>(); // pure pricing (IClock + EconomyConfig)

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
                scope.ServiceProvider.GetRequiredService<OperationsService>().ReconcileAsync(pilot.CompanyId).GetAwaiter().GetResult();
        }

        MapEndpoints(app, uiPath);
        return app;
    }

    // Bring the database up to the current schema before the app serves a request. A fresh DB gets the
    // full InitialCreate; an already-migrated DB is a no-op (its save intact). A DB from a pre-migrations
    // build (EnsureCreated) has the tables but no migration history, so a plain Migrate() would crash
    // re-creating them ("table already exists") — such a pre-release save is disposable, so it's rebuilt
    // clean. Exposed for the startup-robustness tests.
    public static void PrepareDatabase(CallsignDbContext db)
    {
        if (IsLegacyEnsureCreatedDatabase(db))
            RetireLegacyDatabase(db);
        db.Database.Migrate();
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
            return Results.Ok(new StateDto(pilot.Name, pilot.Rank.ToString(), pilot.Xp, pilot.ReputationMilli,
                pilot.CurrentIcao, pilot.HomeIcao, company.CashCents, company.Cash, flights));
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
            return Results.Ok(jobs.Select(j =>
            {
                // Shown on the board, but locked with the reason (rank — 3b, or reputation — 3e/3f).
                var def = MissionCatalog.Def(j.Type);
                bool rankLocked = pilot.Rank < j.RequiredRank;
                bool repLocked = pilot.ReputationMilli < def.MinReputationMilli;
                var reqName = RankTiers.Def(j.RequiredRank).DisplayName;
                string? reason = rankLocked ? $"Requires {reqName}"
                    : repLocked ? $"Requires reputation {def.MinReputationMilli / 1000.0:0.0}"
                    : null;
                return new JobDto(j.Id, j.Type.ToString(), j.OriginIcao, j.DestIcao,
                    names.GetValueOrDefault(j.DestIcao, j.DestIcao), j.Commodity, j.WeightLbs, j.Pax, j.DistanceNm,
                    j.RewardCents, j.Xp, reqName, rankLocked || repLocked, reason, j.ExpiresAt);
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

        app.MapGet("/api/assignments", async (CallsignDbContext db) =>
        {
            var list = await db.JobAssignments.Where(a => a.Status == AssignmentStatus.Accepted).ToListAsync();
            var names = await AirportNamesAsync(db, list.Select(a => a.DestIcao));
            return Results.Ok(list.Select(a => new AssignmentDto(a.Id, a.Type.ToString(), a.OriginIcao, a.DestIcao,
                names.GetValueOrDefault(a.DestIcao, a.DestIcao), a.Commodity, a.WeightLbs, a.Pax, a.DistanceNm, a.RewardQuoteCents, a.XpQuote, a.Status.ToString())));
        });

        app.MapPost("/api/assignments/{id:guid}/settle", async (Guid id, FlightResultDto dto, SettlementService svc) =>
        {
            var record = new Callsign.Core.Flight.FlightRecord(
                dto.AircraftTitle, dto.DepartedAt, dto.ArrivedAt, dto.TouchdownFpm, dto.MaxAltitudeFt,
                dto.DepartureLat, dto.DepartureLon, dto.ArrivalLat, dto.ArrivalLon, dto.DistanceNm, dto.FuelUsedLbs, []);
            var r = await svc.SettleAsync(id, record);
            return Results.Ok(new SettlementDto(r.FlightId, r.PayoutCents, r.XpAwarded, r.PayloadMatched,
                r.PromotedTo is { } pr ? RankTiers.Def(pr).DisplayName : null,
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

        app.MapGet("/api/aircraft", async (CallsignDbContext db, AircraftDealerService dealer, QualificationService quals) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var hangar = await dealer.GetHangarAsync(pilot.CompanyId);
            var held = await quals.HeldClassesAsync(pilot.Id); // which licence classes the pilot holds (3c)
            return Results.Ok(hangar.Select(h =>
            {
                var reqd = QualificationClasses.ForCategory(h.Type.Category);
                return new OwnedAircraftDto(
                    h.Instance.Id, h.Instance.Tail, h.Type.CanonicalName, h.Type.Category.ToString(),
                    h.Instance.LocationIcao, h.Instance.Availability.ToString(),
                    h.Instance.PurchasePriceCents, h.Instance.AirframeHours,
                    h.Instance.HullConditionMilli, h.Instance.EngineConditionMilli,
                    dealer.MaintenanceDue(h.Instance), dealer.MaintenanceQuoteCents(h.Instance),
                    QualificationClasses.Def(reqd).DisplayName, held.Contains(reqd));
            }));
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

        // --- Staff + standing orders (Phase 2d) ---
        app.MapGet("/api/staff/candidates", async (CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            return Results.Ok(ops.GenerateCandidates(pilot.CompanyId.GetHashCode())
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

        app.MapGet("/api/ops/orders", async (CallsignDbContext db) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var orders = await db.StandingOrders.Where(o => o.CompanyId == pilot.CompanyId && o.IsActive && !o.IsDeleted).ToListAsync();
            var staffNames = await db.Staff.Where(s => s.CompanyId == pilot.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name);
            var tails = await db.AircraftInstances.Where(a => a.CompanyId == pilot.CompanyId).ToDictionaryAsync(a => a.Id, a => a.Tail);
            return Results.Ok(orders.Select(o => new StandingOrderDto(o.Id,
                staffNames.GetValueOrDefault(o.StaffId, "?"), tails.GetValueOrDefault(o.AircraftInstanceId, "?"),
                o.OriginIcao, o.DestIcao, o.DistanceNm, o.RoundTripHours, o.RewardPerTripCents)));
        });

        app.MapPost("/api/ops/orders", async (StandingOrderRequest req, CallsignDbContext db, OperationsService ops) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            try
            {
                var o = await ops.CreateStandingOrderAsync(pilot.CompanyId, req.StaffId, req.AircraftInstanceId, req.DestIcao);
                return Results.Ok(new { id = o.Id, roundTripHours = o.RoundTripHours, rewardPerTripCents = o.RewardPerTripCents });
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
            return Results.Ok(new ReconcileDto(d.Trips, d.GrossIncomeCents, d.FeesCents, d.WagesCents, d.RentCents, d.LoanCents, d.InsuranceCents, d.NetCents));
        });

        // --- Loans (Phase 4a) ---
        app.MapGet("/api/loans", async (CallsignDbContext db, LoanService loans) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var active = await loans.GetActiveAsync(pilot.CompanyId);
            return Results.Ok(new
            {
                loans = active.Select(l => new LoanDto(l.Id, l.Tier, l.PrincipalCents, l.OutstandingCents,
                    l.AprBps, l.TermDays, l.Status.ToString(), l.TakenAt)),
                offers = loans.Offers().Select(t => new LoanOfferDto(t.Tier, t.Name, t.MinPrincipalCents, t.MaxPrincipalCents, t.AprBps)),
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
        app.MapGet("/api/routes", async (CallsignDbContext db, RouteService routes, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var active = await routes.GetRoutesAsync(pilot.CompanyId);
            var baseViews = await bases.GetBasesAsync(pilot.CompanyId);
            var missions = MissionCatalog.All.Where(d => d.MinReputationMilli == 0 && d.ReputationMilliReward >= 0)
                .Select(d => d.Type.ToString()).ToList();
            return Results.Ok(new
            {
                routes = active.Select(r => new RouteDto(r.Id, r.Name, r.OriginIcao, r.DestIcao, r.Mission.ToString(),
                    r.DistanceNm, r.RoundTripHours, r.RewardPerTripCents)),
                bases = baseViews.Select(b => new RouteBaseDto(b.Icao, b.Name)),
                missions,
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
                var r = await routes.CreateRouteAsync(pilot.CompanyId, req.Name, req.OriginIcao, req.DestIcao, req.AircraftInstanceId, req.StaffId, mission);
                return Results.Ok(new { id = r.Id, name = r.Name, rewardPerTripCents = r.RewardPerTripCents });
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
        app.MapGet("/api/bases", async (CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var list = await bases.GetBasesAsync(pilot.CompanyId);
            return Results.Ok(list.Select(b => new BaseViewDto(b.Id, b.Icao, b.Name, b.IsHome, b.RentPerDayCents)));
        });

        app.MapGet("/api/bases/candidates", async (CallsignDbContext db, BaseService bases) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            var offers = await bases.GetCandidatesAsync(pilot.CompanyId);
            return Results.Ok(offers.Select(o => new BaseOfferDto(o.Icao, o.Name, o.Kind, o.DistanceNm, o.OpenCents, o.RentPerDayCents)));
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

        // --- Trade (Phase 2g): the market is priced at your current airport ---
        app.MapGet("/api/trade/market", async (CallsignDbContext db, MarketService market) =>
        {
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is null) return Results.NotFound();
            return Results.Ok(market.Quotes(pilot.CurrentIcao)
                .Select(q => new MarketQuoteDto(q.Good, q.Name, q.BuyCents, q.SellCents, q.UnitWeightLbs)));
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

        app.MapGet("/api/flights", async (CallsignDbContext db) =>
        {
            var flights = await db.Flights.OrderByDescending(f => f.SettledAt).Take(50).ToListAsync();
            return Results.Ok(flights.Select(f => new FlightDto(f.Id, f.AircraftTitle, f.TouchdownFpm, f.PayoutCents, f.Xp, f.SettledAt)));
        });

        // --- Live flight: begin tracking an accepted assignment; the next landing auto-settles it ---
        app.MapPost("/api/flight/begin", async (BeginFlightRequest req, CallsignDbContext db, QualificationService quals, FlightSessionService session) =>
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
            return Results.Ok(new AirlineDto(
                new AirlineIdentityDto(id.Name, id.TailCode, id.AccentColorHex, id.EmblemKey, id.Customised),
                new AirlineStandingDto(st.Tier, st.TierName, st.Score, st.NextTierScore,
                    st.Contributions.Select(c => new StandingContributionDto(c.Label, c.Points)).ToList()),
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
            var info = await saves.BackupAsync(db, DateTime.UtcNow);
            return Results.Ok(new { info.Name, info.SizeBytes, info.CreatedUtc });
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
