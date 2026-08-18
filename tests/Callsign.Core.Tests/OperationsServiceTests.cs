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
        Assert.Equal(digest.GrossIncomeCents - digest.FeesCents - digest.WagesCents - digest.RentCents, digest.NetCents);
        Assert.True(digest.WagesCents > 0);

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
}
