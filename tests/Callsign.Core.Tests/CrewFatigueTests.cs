using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Crew fatigue (Phase 16d): a hard-run crew tires and flies worse; a Chief Pilot's rostering eases it.</summary>
public class CrewFatigueTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    // Seed an incorporated company running one autonomous line with a fixed-skill crew; optionally a Chief Pilot
    // and a pre-set crew fatigue. Returns the ids so the caller can reconcile and inspect.
    private static async Task<(Guid cid, Guid crewId)> SeedLineAsync(TestDb tdb, FakeClock clock, EconomyConfig cfg,
        int crewSkill = 60_000, int chiefPilotComp = -1, int startFatigue = 0)
    {
        Guid cid, crewId, aircraftId;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Co", AirlineName = "Co Air" }; // incorporated
            db.Companies.Add(c);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = c.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
            db.AircraftInstances.Add(inst);
            var crew = new Staff { Id = Guid.NewGuid(), CompanyId = c.Id, Name = "Crew", Role = StaffRole.Pilot, SkillMilli = crewSkill, FatigueMilli = startFatigue, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(crew);
            if (chiefPilotComp >= 0)
                db.Executives.Add(new Executive { Id = Guid.NewGuid(), CompanyId = c.Id, Role = ExecutiveRole.ChiefPilot, Name = "CP", CompetenceMilli = chiefPilotComp, SalaryPerDayCents = 50_000, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow, IsActive = true });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, 1_000_000m, "start");
            cid = c.Id; crewId = crew.Id; aircraftId = inst.Id;
        }
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, cfg).CreateStandingOrderAsync(cid, crewId, aircraftId, "EHRD");
        return (cid, crewId);
    }

    [Fact]
    public async Task Fatigue_AccruesWhenACrewFliesHard()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (_, crewId) = await SeedLineAsync(tdb, clock, Cfg);
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync((await db.Staff.FindAsync(crewId))!.CompanyId);
        using (var db = tdb.NewContext())
            Assert.True((await db.Staff.FindAsync(crewId))!.FatigueMilli > 0); // duty at the cap tired them
    }

    [Fact]
    public async Task Fatigue_AChiefPilotEasesIt()
    {
        async Task<int> FatigueAfterAsync(int chiefPilotComp)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var (cid, crewId) = await SeedLineAsync(tdb, clock, Cfg, chiefPilotComp: chiefPilotComp);
            clock.UtcNow = clock.UtcNow.AddDays(2);
            using (var db = tdb.NewContext())
                await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(cid);
            using (var db = tdb.NewContext())
                return (await db.Staff.FindAsync(crewId))!.FatigueMilli;
        }
        int without = await FatigueAfterAsync(-1);          // no Chief Pilot
        int withAce = await FatigueAfterAsync(100_000);     // an ace Chief Pilot rosters the rest in
        Assert.True(without > 0);
        Assert.True(withAce < without);                     // rostering eased the fatigue
    }

    // A pre-fatigued crew flies with less effective skill, so the same line reputes LESS than a fresh crew's.
    [Fact]
    public async Task Fatigue_PenalisesPerformance()
    {
        var cfg = Cfg with { OperatingRepAutoMaxStepPerPassMilli = 100_000 }; // let the skill difference (not the cap) govern the one pass
        async Task<int> RepDeltaAsync(int startFatigue)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var (cid, _) = await SeedLineAsync(tdb, clock, cfg, crewSkill: 80_000, startFatigue: startFatigue);
            clock.UtcNow = clock.UtcNow.AddDays(2);
            using var db = tdb.NewContext();
            return (await new OperationsService(db, new LedgerService(db, clock), clock, cfg).ReconcileAsync(cid)).OperatingRepDeltaMilli;
        }
        int fresh = await RepDeltaAsync(0);
        int wrecked = await RepDeltaAsync(100_000);
        Assert.True(fresh > 0);
        Assert.True(fresh > wrecked); // exhaustion cost the name
    }
}
