using Callsign.Core.Aircraft;
using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

/// <summary>Phase 11c — the airline's operating reputation PAYS at your hubs (job pay + offer count, route +
/// standing-order pay), all income-only, hub-scoped, frozen at posting/creation. These lock the lift AND the
/// no-pump story: rep-0 / off-hub is byte-identical to pre-11c, settlement reads the frozen quote (not live
/// rep), and the two-sided commodity market is provably untouched.</summary>
public class HubReputationTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Airport A(string id, string name, double lat, double lon, AirportKind kind = AirportKind.LargeAirport)
        => new() { Ident = id, IcaoCode = id, Name = name, Latitude = lat, Longitude = lon, Kind = kind };

    private static async Task SeedAirportsAsync(CallsignDbContext db)
    {
        db.Airports.AddRange(
            A("EHAM", "Amsterdam Schiphol", 52.3086, 4.7639),
            A("EHRD", "Rotterdam", 51.9569, 4.4372),
            A("EHEH", "Eindhoven", 51.4501, 5.3745),
            A("EHGG", "Groningen", 53.1197, 6.5794, AirportKind.MediumAirport),
            A("EGLL", "London Heathrow", 51.4706, -0.4619));
        await db.SaveChangesAsync();
    }

    private static JobBoardService Board(CallsignDbContext db, IClock clock)
        => new(db, new AirportRepository(db), new CargoJobSource(Cfg), clock, Cfg, new WorldOracle(Cfg));

    // A company with an active base at baseIcao and the given operating reputation.
    private static async Task<Guid> SeedHubCompanyAsync(CallsignDbContext db, int repMilli, string baseIcao)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = repMilli };
        db.Companies.Add(company);
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = baseIcao, IsActive = true });
        await db.SaveChangesAsync();
        return company.Id;
    }

    // ── Pure curve ──

    [Fact]
    public void HubReputationPayFactor_NeutralAtRepZero()
    {
        Assert.Equal(1.0, Cfg.HubReputationPayFactor(0));      // the floor is exactly today's economics
        Assert.Equal(8, Cfg.HubReputationOfferCount(8, 0));
    }

    [Fact]
    public void HubReputation_RampsLinearToMax()
    {
        Assert.Equal(1.15, Cfg.HubReputationPayFactor(100_000), 6);
        Assert.Equal(1.075, Cfg.HubReputationPayFactor(50_000), 6);
        Assert.Equal(12, Cfg.HubReputationOfferCount(8, 100_000)); // 8 × 1.5
        Assert.Equal(10, Cfg.HubReputationOfferCount(8, 50_000));  // 8 × 1.25
    }

    // ── Job board: at-hub freezes the lift; off-hub / rep-0 are byte-identical to pre-11c ──

    [Fact]
    public async Task Jobs_AtHub_FreezeLiftedRewardAndCount()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId;
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);
        using (var db = tdb.NewContext()) companyId = await SeedHubCompanyAsync(db, 100_000, "EHAM");

        int posted;
        using (var db = tdb.NewContext())
            posted = await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, count: 8, seed: 1, companyId);
        Assert.Equal(12, posted); // a flawless name at your hub widens the board 8 → 12

        using (var db = tdb.NewContext())
        {
            var jobs = await Board(db, clock).GetAvailableAsync("EHAM");
            Assert.Equal(12, jobs.Count);
            double demand = new WorldOracle(Cfg).EconomyPhaseAt(T0).DemandMult * Cfg.SeasonalDemandFactor(GameCalendar.Season(T0, 52.3086));
            Assert.All(jobs, j =>
            {
                // Frozen at posting: base × macro demand × hub reputation lift (1.15).
                long expected = (long)Math.Round(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs) * demand * Cfg.HubReputationPayFactor(100_000));
                Assert.Equal(expected, j.RewardCents);
            });
        }
    }

    [Fact]
    public async Task Jobs_OffHub_ByteIdentical()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId;
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);
        using (var db = tdb.NewContext()) companyId = await SeedHubCompanyAsync(db, 100_000, "EHRD"); // base is EHRD, NOT EHAM

        using (var db = tdb.NewContext())
            await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, count: 8, seed: 1, companyId); // refresh AWAY from the hub

        using (var db = tdb.NewContext())
        {
            var jobs = await Board(db, clock).GetAvailableAsync("EHAM");
            Assert.Equal(8, jobs.Count); // no widening off-hub
            double demand = new WorldOracle(Cfg).EconomyPhaseAt(T0).DemandMult * Cfg.SeasonalDemandFactor(GameCalendar.Season(T0, 52.3086));
            Assert.All(jobs, j => Assert.Equal((long)Math.Round(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs) * demand), j.RewardCents)); // no lift — pre-11c formula
        }
    }

    [Fact]
    public async Task Jobs_FreshCompanyAtHub_ByteIdentical()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId;
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);
        using (var db = tdb.NewContext()) companyId = await SeedHubCompanyAsync(db, 0, "EHAM"); // a base at the origin, but an UNEARNED name

        using (var db = tdb.NewContext())
            await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, count: 8, seed: 1, companyId);

        using (var db = tdb.NewContext())
        {
            var jobs = await Board(db, clock).GetAvailableAsync("EHAM");
            Assert.Equal(8, jobs.Count); // rep 0 → no widening even at a hub (the floor is the floor, not the knob)
            double demand = new WorldOracle(Cfg).EconomyPhaseAt(T0).DemandMult * Cfg.SeasonalDemandFactor(GameCalendar.Season(T0, 52.3086));
            Assert.All(jobs, j => Assert.Equal((long)Math.Round(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs) * demand), j.RewardCents));
        }
    }

    // ── Freeze survives a later reputation change: settlement reads the frozen quote, never live rep ──

    [Fact]
    public async Task Settlement_IgnoresReputationForPay()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId, pilotId, jobId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" }; // rep 0 at accept time
            var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
            var type = new AircraftType
            {
                Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172 Skyhawk", Category = AircraftCategory.LightSingle, UsefulLoadLbs = 878,
                Aliases = [new AircraftTitleAlias { Title = "Cessna 172 Skyhawk", TitleNormalized = AircraftTitle.Normalize("Cessna 172 Skyhawk") }],
            };
            var job = new Job
            {
                Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "parts",
                WeightLbs = 500, RewardCents = 200_000, Xp = 20, DistanceNm = 120, RequiredRank = PilotRank.Trainee,
                GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
            };
            db.AddRange(company, pilot, type, job);
            await db.SaveChangesAsync();
            companyId = company.Id; pilotId = pilot.Id; jobId = job.Id;
        }

        Guid assignmentId;
        using (var db = tdb.NewContext())
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(jobId, companyId, pilotId)).Id; // freezes RewardQuoteCents = 200_000

        // Now the airline's name soars — but the leg was already priced.
        using (var db = tdb.NewContext())
        {
            (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli = 100_000;
            await db.SaveChangesAsync();
        }

        SettlementResult r;
        using (var db = tdb.NewContext())
            r = await new SettlementService(db, new LedgerService(db, clock), clock, Cfg).SettleAsync(
                assignmentId, new FlightRecord("Cessna 172 Skyhawk", T0.AddMinutes(5), T0.AddMinutes(55), -80, 9000, 52.3, 4.76, 51.95, 4.44, 120, 60, []));

        Assert.Equal(220_000, r.PayoutCents); // 200_000 × 1.10 greaser — NOT × 1.15; the pumped name never touches owed pay
    }

    // ── Routes: lifted + frozen at creation ──

    private static async Task<(Guid CompanyId, Guid AircraftId, Guid StaffId)> SeedRouteCompanyAsync(TestDb tdb, IClock clock, int repMilli)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = repMilli };
        db.Companies.Add(company);
        db.Airports.Add(A("EHAM", "Schiphol", 52.31, 4.76));
        db.Airports.Add(A("EHRD", "Rotterdam", 51.95, 4.44, AirportKind.MediumAirport));
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true });
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 60_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.AddRange(type, inst, staff);
        await db.SaveChangesAsync();
        return (company.Id, inst.Id, staff.Id);
    }

    [Fact]
    public async Task Route_AtHub_FreezesLiftedReward()
    {
        // Each company in its own db (shared airport/type keys would collide otherwise).
        static async Task<Route> RouteAtRep(int rep)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var (companyId, aircraftId, staffId) = await SeedRouteCompanyAsync(tdb, clock, rep);
            using var db = tdb.NewContext();
            return await new RouteService(db, clock, Cfg).CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);
        }

        var r0 = await RouteAtRep(0);
        var r1 = await RouteAtRep(100_000);

        long baseReward = Cfg.CargoRewardCents(r1.DistanceNm, (Cfg.MinCargoWeightLbs + Cfg.MaxCargoWeightLbs) / 2);
        long expected = (long)Math.Round(baseReward * MissionCatalog.Def(MissionType.Cargo).RewardMult * Cfg.HubReputationPayFactor(100_000));
        Assert.Equal(expected, r1.RewardPerTripCents);          // exact lifted rate
        Assert.True(r1.RewardPerTripCents > r0.RewardPerTripCents); // strictly more than the rep-0 route
    }

    [Fact]
    public async Task Route_FrozenAcrossReputationDrift()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedRouteCompanyAsync(tdb, clock, 100_000);

        Guid routeId; long frozen;
        using (var db = tdb.NewContext())
        {
            var r = await new RouteService(db, clock, Cfg).CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);
            routeId = r.Id; frozen = r.RewardPerTripCents;
        }
        // The name later collapses to nothing — the struck-deal annuity stands.
        using (var db = tdb.NewContext())
        {
            (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli = 0;
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
            Assert.Equal(frozen, (await db.Routes.FindAsync(routeId))!.RewardPerTripCents); // frozen at creation, never re-rated
    }

    // ── Standing orders: hub origin lifts, non-hub origin is neutral ──

    private static async Task<Guid> SeedOrderCompanyAsync(TestDb tdb, IClock clock, int repMilli, string aircraftLocationIcao, bool baseAtLocation)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = repMilli };
        db.Companies.Add(company);
        db.Airports.Add(A(aircraftLocationIcao, "Origin", 52.31, 4.76));
        db.Airports.Add(A("EHRD", "Rotterdam", 51.95, 4.44, AirportKind.MediumAirport));
        if (baseAtLocation)
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = aircraftLocationIcao, IsActive = true });
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = aircraftLocationIcao, Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 60_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.AddRange(type, inst, staff);
        await db.SaveChangesAsync();
        return company.Id;
    }

    [Fact]
    public async Task StandingOrder_HubOriginLifts_NonHubOriginNeutral()
    {
        // Same rep (100), same route — only difference is whether the departure field is one of my bases.
        static async Task<long> OrderReward(bool baseAtLocation)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var companyId = await SeedOrderCompanyAsync(tdb, clock, 100_000, "EHAM", baseAtLocation);
            using var db = tdb.NewContext();
            var aid = await db.AircraftInstances.Where(a => a.CompanyId == companyId).Select(a => a.Id).FirstAsync();
            var sid = await db.Staff.Where(s => s.CompanyId == companyId).Select(s => s.Id).FirstAsync();
            return (await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(companyId, sid, aid, "EHRD")).RewardPerTripCents;
        }

        long hubReward = await OrderReward(baseAtLocation: true);
        long awayReward = await OrderReward(baseAtLocation: false); // aircraft at EHAM, but EHAM is NOT a base

        Assert.True(hubReward > awayReward);                                   // a hub origin lifts the frozen pay
        Assert.Equal((long)Math.Round(awayReward * Cfg.HubReputationPayFactor(100_000)), hubReward); // exactly the 1.15 lift over the non-hub baseline
    }

    // ── The two-sided commodity market is provably untouched by reputation ──

    [Fact]
    public async Task Market_IndependentOfReputation()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid richCo, poorCo;
        using (var db = tdb.NewContext())
        {
            db.Airports.Add(A("EHAM", "Schiphol", 52.31, 4.76));
            richCo = await SeedHubCompanyAsync(db, 100_000, "EHAM");
            poorCo = await SeedHubCompanyAsync(db, 0, "EHAM");
        }

        TradeService Trade(CallsignDbContext db) => new(db, new LedgerService(db, clock), new MarketService(clock, Cfg), clock, Cfg, new WorldOracle(Cfg));
        IReadOnlyList<MarketQuote> rich, poor;
        using (var db = tdb.NewContext()) rich = await Trade(db).GetMarketAsync(richCo, "EHAM");
        using (var db = tdb.NewContext()) poor = await Trade(db).GetMarketAsync(poorCo, "EHAM");

        Assert.Equal(rich.Count, poor.Count);
        for (int i = 0; i < rich.Count; i++)
        {
            Assert.Equal(rich[i].BuyCents, poor[i].BuyCents);   // reputation is never a Quote argument — the ≤2×spread guard is preserved
            Assert.Equal(rich[i].SellCents, poor[i].SellCents);
        }
    }

    // ── The bigger frozen reward still reconciles exactly ──

    [Fact]
    public async Task Reconcile_LedgerExact_AfterRepLiftedRoute()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedRouteCompanyAsync(tdb, clock, 100_000);
        using (var db = tdb.NewContext())
            await new LedgerService(db, clock).PostAsync(companyId, LedgerCategory.StartingBalance, 100_000m, "start");
        using (var db = tdb.NewContext())
            await new RouteService(db, clock, Cfg).CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);

        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // the invariant holds through a reputation-lifted route
        }
    }
}
