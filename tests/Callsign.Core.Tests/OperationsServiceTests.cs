using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class OperationsServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    [Fact]
    public async Task Reconcile_BooksTrips_Fees_AndWages_ViaLedger_AndIsIdempotent()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, staffId, aircraftId;

        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id;
            aircraftId = inst.Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            var candidate = ops.GenerateCandidates(companyId.GetHashCode())[0];
            staffId = (await ops.HireAsync(companyId, candidate.Seed)).Id;
            await ops.CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");
        }

        clock.UtcNow = clock.UtcNow.AddDays(2); // two days pass while the app is "closed"

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.True(digest.Trips > 0);
        Assert.Equal(digest.GrossIncomeCents - digest.FeesCents - digest.WagesCents - digest.RentCents - digest.FuelCents, digest.NetCents);
        Assert.True(digest.WagesCents > 0);
        Assert.True(digest.FuelCents > 0); // Wave-2 — autonomous trips now bill fuel

        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // the invariant survives autonomous income
            var cats = (await db.LedgerEntries.ToListAsync()).Select(e => e.Category).ToHashSet();
            Assert.Contains(LedgerCategory.StaffWage, cats);
            Assert.Contains(LedgerCategory.AirportFee, cats);
            Assert.True((await db.AircraftInstances.FindAsync(aircraftId))!.AirframeHours > 0); // the airframe flew
        }

        using (var db = tdb.NewContext())
        {
            var again = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
            Assert.Equal(0, again.Trips); // no time passed since → nothing double-booked
        }
    }

    [Fact]
    public async Task Reconcile_HoldsAGroundedTail_AndWarns_WithoutFlyingIt()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, staffId, aircraftId;

        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            // Sub-floor condition ⇒ the tail is grounded before it can fly the order.
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", HullConditionMilli = 10_000 };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id;
            aircraftId = inst.Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            var candidate = ops.GenerateCandidates(companyId.GetHashCode())[0];
            staffId = (await ops.HireAsync(companyId, candidate.Seed)).Id;
            await ops.CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");
        }

        clock.UtcNow = clock.UtcNow.AddDays(2);

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.Equal(0, digest.Trips);                                 // the grounded tail flew nothing
        Assert.Contains(digest.Grounded, g => g.Contains("CS-1"));     // and the player is warned
        using (var db = tdb.NewContext())
            Assert.Equal(0, (await db.AircraftInstances.FindAsync(aircraftId))!.AirframeHours); // it never flew
    }

    [Fact]
    public async Task Reconcile_PilotSkill_ChangesTheOutcome()
    {
        // Same route, same window — but a green crew botches many trips (diversions) where an ace botches
        // almost none. Skill finally touches the autonomous economy (Phase 7f).
        static async Task<ReconcileDigest> Run(int skillMilli)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            Guid companyId, aircraftId, staffId;
            using (var db = tdb.NewContext())
            {
                var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
                db.Companies.Add(company);
                db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
                var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
                db.AircraftTypes.Add(type);
                var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
                db.AircraftInstances.Add(inst);
                var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Pilot", SkillMilli = skillMilli, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
                db.Staff.Add(staff);
                await db.SaveChangesAsync();
                await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
                companyId = company.Id; aircraftId = inst.Id; staffId = staff.Id;
            }
            using (var db = tdb.NewContext())
                await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");
            clock.UtcNow = clock.UtcNow.AddDays(10); // enough elapsed time for many trips
            using (var db = tdb.NewContext())
                return await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
        }

        var green = await Run(10_000); // 10% skill
        var ace = await Run(98_000);   // 98% skill
        Assert.True(green.Incidents > 0);              // a green crew definitely botches some
        Assert.True(green.Incidents > ace.Incidents);  // and far more than an ace
    }

    [Fact]
    public async Task Reconcile_CapsToOneCrewsDuty_AndNudgesToHireMore()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, aircraftId, staffId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Pilot", SkillMilli = 90_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id; aircraftId = inst.Id; staffId = staff.Id;
        }
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");

        clock.UtcNow = clock.UtcNow.AddDays(4); // 96 h elapsed — non-stop that would be ~96 airframe hours

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.Contains(digest.DutyMaxed, t => t.Contains("CS-1")); // the lone crew hit their limit
        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(aircraftId);
            Assert.True(inst!.AirframeHours > 0);                              // it did fly
            Assert.True(inst.AirframeHours <= Cfg.MaxDutyHoursPerDay * 4 + 2); // but only ~8 h/day, not round the clock
        }
    }

    [Fact]
    public async Task CreateStandingOrder_RefusesAPilotAlreadyFlyingAnotherLine()
    {
        // Otherwise one pilot on two tails would fly the daily duty cap on EACH, doubling autonomous income
        // and defeating the FTL limit — one crew flies one line.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, staffId, tailA, tailB;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var i1 = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            var i2 = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-2", LocationIcao = "EHAM" };
            db.AircraftInstances.AddRange(i1, i2);
            var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Solo", SkillMilli = 50_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            companyId = company.Id; staffId = staff.Id; tailA = i1.Id; tailB = i2.Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            await ops.CreateStandingOrderAsync(companyId, staffId, tailA, "EHRD");  // first line: fine
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ops.CreateStandingOrderAsync(companyId, staffId, tailB, "EHRD"));   // same pilot, second tail: refused
        }
    }

    [Fact]
    public async Task Reconcile_HiredCrew_SharpensWithExperience()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, staffId, aircraftId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Rookie", SkillMilli = 30_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id; staffId = staff.Id; aircraftId = inst.Id;
        }
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");

        clock.UtcNow = clock.UtcNow.AddDays(3);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            var staff = await db.Staff.FindAsync(staffId);
            Assert.True(staff!.SkillMilli > 30_000);                     // the rookie improved with time in the seat
            Assert.True(staff.SkillMilli <= Cfg.CrewSkillCeilingMilli);  // but experience tops out below perfect
        }
    }

    [Fact]
    public async Task CreateStandingOrder_GatesByCrewTypeRating()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, aircraftId, greenId, aceId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "PC12", CanonicalName = "Pilatus PC-12", Category = AircraftCategory.Turboprop, CruiseKtas = 270, UsefulLoadLbs = 1_000 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            var green = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Green", SkillMilli = 40_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            var ace = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Ace", SkillMilli = 80_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.AddRange(green, ace);
            await db.SaveChangesAsync();
            companyId = company.Id; aircraftId = inst.Id; greenId = green.Id; aceId = ace.Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            // A turboprop needs 50% — the 40% green pilot is refused...
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateStandingOrderAsync(companyId, greenId, aircraftId, "EHRD"));
            // ...but the 80% ace can be trusted with it.
            Assert.NotNull(await ops.CreateStandingOrderAsync(companyId, aceId, aircraftId, "EHRD"));
        }
    }

    [Fact]
    public void RollTrips_IncidentsLandOnSeverityTiers_NotAllTheSame()
    {
        // Force every trip to be an incident (100% base rate, 0 skill) so income reflects the tier mix:
        // ~65% minor (×0.9 pay) + ~27% diversion (×0.5) + ~8% major (×0) ≈ 0.72 of the full fee — proving
        // incidents are NOT all identical diversions (which would give 0.5) nor all minor (0.9).
        var cfg = EconomyConfig.Default with { BaseIncidentRatePct = 1.0 };
        const int trips = 2_000;
        const long reward = 100_000;
        var (income, incidents, wear, empty) = OperationsService.RollTrips(cfg, Guid.NewGuid(), 12_345L, trips, 0, reward);

        Assert.Equal(trips, incidents);   // at a 100% rate every trip is an incident
        Assert.True(wear > 0);
        Assert.Equal(0, empty);           // at the fair price the client fills every trip
        long full = (long)trips * reward;
        Assert.InRange(income, (long)(full * 0.66), (long)(full * 0.78)); // the tier mix, centred on ~0.72
    }

    [Fact]
    public void RollTrips_Markup_IncidentRollFiresOnlyOnFilledTrips()
    {
        // The demand roll and the incident roll are independent streams, and an empty leg is skipped BEFORE the
        // incident roll. So at a 100% incident rate every FILLED trip is an incident and every empty leg is not:
        // incidents == filled == trips − empty. At the fair price nothing is empty (the original path); at a
        // markup the incident count tracks the filled count exactly, proving empties never reach the incident roll.
        var cfg = EconomyConfig.Default with { BaseIncidentRatePct = 1.0 };
        const int trips = 2_000;
        var id = Guid.NewGuid();

        var fair = OperationsService.RollTrips(cfg, id, 999L, trips, 0, 100_000, 1000);
        Assert.Equal(0, fair.Empty);
        Assert.Equal(trips, fair.Incidents);                    // fair price fills every trip → every trip an incident

        var marked = OperationsService.RollTrips(cfg, id, 999L, trips, 0, 100_000, 1500);
        Assert.True(marked.Empty > 0);                          // the premium leaves legs empty
        Assert.Equal(trips - marked.Empty, marked.Incidents);   // incident roll fired on exactly the filled legs
    }

    [Fact]
    public void RollTrips_Markup_RaisesFilledPay_ButLeavesLegsEmpty()
    {
        // A premium price: filled trips pay more per trip, but a share of legs fly empty (no pay). No incidents
        // (0% rate) so income is purely (filled trips × marked reward) and the empties are the demand shortfall.
        var cfg = EconomyConfig.Default with { BaseIncidentRatePct = 0.0 };
        const int trips = 2_000;
        const long reward = 100_000;
        var id = Guid.NewGuid();

        var fair = OperationsService.RollTrips(cfg, id, 7L, trips, 60_000, reward, 1000);
        var marked = OperationsService.RollTrips(cfg, id, 7L, trips, 60_000, reward, 1500); // +50% → ~70% fill

        Assert.Equal(0, fair.Empty);
        Assert.Equal(trips * reward, fair.Income);
        Assert.InRange(marked.Empty, (int)(trips * 0.20), (int)(trips * 0.40));   // ~30% of legs run empty at +50%
        int filled = trips - marked.Empty;
        Assert.Equal(filled * (long)Math.Round(reward * 1.5), marked.Income);     // each filled trip pays the marked rate
        Assert.True(marked.Income > fair.Income);                                  // at these knobs the premium still nets more
    }

    [Fact]
    public async Task StandingOrderMarkup_ClampsAtCreate_AndRepriceClampsToo()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, aircraftId, aceId, orderId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "PC12", CanonicalName = "Pilatus PC-12", Category = AircraftCategory.Turboprop, CruiseKtas = 270, UsefulLoadLbs = 1_000 };
            db.AircraftTypes.Add(type);
            db.AircraftInstances.Add(new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" });
            db.Staff.Add(new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Ace", SkillMilli = 90_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow });
            await db.SaveChangesAsync();
            companyId = company.Id;
            aircraftId = (await db.AircraftInstances.FirstAsync()).Id;
            aceId = (await db.Staff.FirstAsync()).Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            var o = await ops.CreateStandingOrderAsync(companyId, aceId, aircraftId, "EHRD", 9_000); // absurd markup
            orderId = o.Id;
            Assert.Equal(Cfg.MaxContractMarkupMilli, o.PriceMultiplierMilli); // clamped to the cap
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            int m = await ops.SetOrderPriceAsync(companyId, orderId, 500); // below fair → clamps up to fair
            Assert.Equal(1000, m);
            Assert.Equal(1000, (await db.StandingOrders.FindAsync(orderId))!.PriceMultiplierMilli);
        }
    }

    [Fact]
    public async Task Reprice_BooksPendingTripsAtOldPrice_NotRetroactively()
    {
        // Re-pricing must reconcile FIRST at the OLD rate, so trips already flown are never re-priced up (that
        // would be free money). Compared against a plain reconcile over the same window: a reprice-to-+50% books
        // the SAME payout as no reprice — because the pending trips settle at the fair rate before the markup lands.
        var cfg = Cfg with { BaseIncidentRatePct = 0.0 }; // no incidents → income is exactly filled × price

        async Task<long> RunAsync(bool reprice)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            Guid companyId, aircraftId, aceId, orderId;
            using (var db = tdb.NewContext())
            {
                var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
                db.Companies.Add(company);
                db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
                var type = new AircraftType { Id = Guid.NewGuid(), Key = "PC12", CanonicalName = "Pilatus PC-12", Category = AircraftCategory.Turboprop, CruiseKtas = 270, UsefulLoadLbs = 1_000 };
                db.AircraftTypes.Add(type);
                db.AircraftInstances.Add(new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" });
                db.Staff.Add(new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Ace", SkillMilli = 90_000, WagePerDayCents = 0, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow });
                await db.SaveChangesAsync();
                companyId = company.Id;
                aircraftId = (await db.AircraftInstances.FirstAsync()).Id;
                aceId = (await db.Staff.FirstAsync()).Id;
            }
            using (var db = tdb.NewContext())
                orderId = (await new OperationsService(db, new LedgerService(db, clock), clock, cfg)
                    .CreateStandingOrderAsync(companyId, aceId, aircraftId, "EHRD")).Id; // starts at the fair rate (1000)

            clock.UtcNow = clock.UtcNow.AddDays(3); // several trips accrue at the fair rate

            using (var db = tdb.NewContext())
            {
                var ops = new OperationsService(db, new LedgerService(db, clock), clock, cfg);
                if (reprice) await ops.SetOrderPriceAsync(companyId, orderId, 1500); // reconciles-first, THEN marks up
                else await ops.ReconcileAsync(companyId);
            }
            using (var db = tdb.NewContext())
                return await db.LedgerEntries.Where(e => e.Category == LedgerCategory.JobPayout).SumAsync(e => e.AmountCents);
        }

        long control = await RunAsync(reprice: false);
        long repriced = await RunAsync(reprice: true);
        Assert.True(control > 0);           // trips actually accrued and paid out
        Assert.Equal(control, repriced);    // repricing booked the pending trips at the OLD fair rate, not +50%
    }

    // ── Phase 11a — autonomous legs ease the airline's OPERATING reputation toward the competence of the crew
    // that flew (L12): a sharp crew lifts the name, a green crew drags it down, bounded per pass, money-neutral. ──

    // A company running one standing order EHAM↔EHRD with a crew of the given skill, starting at startRepMilli.
    private static async Task<Guid> SeedRunningOrderAsync(TestDb tdb, FakeClock clock, int crewSkillMilli, int startRepMilli = 0)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = startRepMilli };
        db.Companies.Add(company);
        db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
        db.AircraftTypes.Add(type);
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
        db.AircraftInstances.Add(inst);
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Pilot", SkillMilli = crewSkillMilli, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(company.Id, staff.Id, inst.Id, "EHRD");
        return company.Id;
    }

    [Fact]
    public async Task Autonomous_PullsReputationTowardCrewSkill()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 60_000, startRepMilli: 20_000);
        clock.UtcNow = clock.UtcNow.AddDays(2);

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            int rep = (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli;
            Assert.True(rep > 20_000);                                            // a sharper crew than the name lifts it
            Assert.True(rep <= 20_000 + Cfg.OperatingRepAutoMaxStepPerPassMilli); // but only by the per-pass step
            var ev = await db.AirlineReputationEvents.SingleAsync(e => e.CompanyId == companyId);
            Assert.Equal(AirlineRepSource.Crew, ev.Source);                       // tagged as crew-driven
            Assert.True(ev.DeltaMilli > 0);
        }
    }

    [Fact]
    public async Task Autonomous_CheapCrew_DragsReputationDown()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 45_000, startRepMilli: 80_000);
        clock.UtcNow = clock.UtcNow.AddDays(2);

        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
            Assert.True((await db.Companies.FindAsync(companyId))!.OperatingReputationMilli < 80_000); // the name falls toward the greener crew (L12)
    }

    [Fact]
    public async Task Autonomous_PerPassStep_IsCapped()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 90_000, startRepMilli: 0);
        clock.UtcNow = clock.UtcNow.AddDays(10); // a huge backlog of trips

        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
            Assert.Equal(Cfg.OperatingRepAutoMaxStepPerPassMilli, (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli); // one pass moves it by at most the cap — no teleport
    }

    [Fact]
    public async Task Autonomous_RepMove_IsIdempotentOnReplay()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 60_000, startRepMilli: 20_000);
        clock.UtcNow = clock.UtcNow.AddDays(2);

        int repAfterFirst;
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
        using (var db = tdb.NewContext())
            repAfterFirst = (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli;

        // Reconcile again with no elapsed time — 0 trips flew, so the name must not move and no event is written.
        ReconcileDigest again;
        using (var db = tdb.NewContext())
            again = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.Equal(0, again.Trips);
        Assert.Equal(0, again.OperatingRepDeltaMilli);
        using (var db = tdb.NewContext())
        {
            Assert.Equal(repAfterFirst, (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli);
            Assert.Single(await db.AirlineReputationEvents.Where(e => e.CompanyId == companyId).ToListAsync()); // still just the first
        }
    }

    [Fact]
    public async Task Reconcile_ReputationMove_IsMoneyNeutral()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 60_000, startRepMilli: 20_000);
        clock.UtcNow = clock.UtcNow.AddDays(2);

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.NotEqual(0, digest.OperatingRepDeltaMilli); // the reputation engine ran this pass...
        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // ...yet the cash invariant holds — no ledger row moved for it
        }
    }

    [Fact]
    public async Task Digest_ReportsOperatingRepMovement()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedRunningOrderAsync(tdb, clock, crewSkillMilli: 60_000, startRepMilli: 20_000);
        clock.UtcNow = clock.UtcNow.AddDays(2);

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            int rep = (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli;
            Assert.Equal(rep - 20_000, digest.OperatingRepDeltaMilli); // the digest reports exactly the net move (L4)
            Assert.True(digest.OperatingRepDeltaMilli > 0);
        }
    }

    [Fact]
    public async Task Autonomous_MultipleLines_NeverOvershootsCrewSkill()
    {
        // Two autonomous lines, each flown by its own ceiling-skill (95%) crew, with the name sitting just below
        // that skill. Each line's per-batch pull is bounded, but their SUM (pre-fix) would carry the name PAST 95%
        // — the L12 upper bound. The trip-weighted-target clamp holds it AT the crews' competence, never above.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = 94_000 };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var i1 = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            var i2 = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-2", LocationIcao = "EHAM" };
            db.AircraftInstances.AddRange(i1, i2);
            var s1 = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Ace1", SkillMilli = 95_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            var s2 = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Ace2", SkillMilli = 95_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.AddRange(s1, s2);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            await ops.CreateStandingOrderAsync(company.Id, s1.Id, i1.Id, "EHRD");
            await ops.CreateStandingOrderAsync(company.Id, s2.Id, i2.Id, "EHRD"); // one crew per line — two lines, two crews
            companyId = company.Id;
        }
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            int rep = (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli;
            Assert.True(rep > 94_000);   // the name rose toward the crews...
            Assert.True(rep <= 95_000);  // ...but never PAST their 95% competence, even summing two full lines (L12)
        }
    }

    [Fact]
    public async Task Manager_AutoServicesDueOwnedTailsAtItsField_AtReconcile_AndIsIdempotent()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, instId;

        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsActive = true, OpenedAt = clock.UtcNow, LastRentBilledAt = clock.UtcNow });
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "PC12", CanonicalName = "PC-12", Category = AircraftCategory.Turboprop, CruiseKtas = 270, UsefulLoadLbs = 2000 };
            db.AircraftTypes.Add(type);
            // Overdue: 80 airframe hours since the last service (interval 50), worn condition.
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available,
                AirframeHours = 80, MaintenanceHoursWatermark = 0, HullConditionMilli = 40_000, EngineConditionMilli = 55_000,
            });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 1_000_000m, "start");
            companyId = company.Id;
            instId = (await db.AircraftInstances.FirstAsync()).Id;
        }

        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).HireManagerAsync(companyId, "EHAM");

        clock.UtcNow = clock.UtcNow.AddDays(1);
        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.True(digest.RepairCents > 0);        // the manager serviced the due tail
        Assert.True(digest.WagesCents > 0);         // and drew their wage
        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(instId);
            Assert.Equal(100_000, inst!.HullConditionMilli);
            Assert.Equal(100_000, inst.EngineConditionMilli);
            Assert.Equal(80.0, inst.MaintenanceHoursWatermark); // watermark reset to current hours
        }

        // Idempotent: nothing new is due next pass, so no more servicing is charged.
        clock.UtcNow = clock.UtcNow.AddDays(1);
        using (var db = tdb.NewContext())
        {
            var d2 = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
            Assert.Equal(0, d2.RepairCents);
        }
    }

    [Fact]
    public async Task HireManager_RequiresABaseAtTheField_AndOnlyOnePerField()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsActive = true, OpenedAt = clock.UtcNow, LastRentBilledAt = clock.UtcNow });
            await db.SaveChangesAsync();
            companyId = company.Id;
        }
        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.HireManagerAsync(companyId, "EHRD")); // no base there
            await ops.HireManagerAsync(companyId, "EHAM");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.HireManagerAsync(companyId, "EHAM")); // already managed
        }
    }
}
