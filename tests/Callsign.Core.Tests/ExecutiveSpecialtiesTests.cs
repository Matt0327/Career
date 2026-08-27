using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 16d specialists — each executive's own bounded lever on the autonomous operation:
/// COO throughput, CFO fuel, Maintenance Director wear. Each proven by A/B reconcile with vs without the hire.</summary>
public class ExecutiveSpecialtiesTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    // Seed an incorporated company with one autonomous line; optionally seat one ace executive.
    private static async Task<(Guid cid, Guid aircraftId)> SeedAsync(TestDb tdb, FakeClock clock, ExecutiveRole? ace)
    {
        Guid cid, crewId, aircraftId;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Co", AirlineName = "Co Air" };
            db.Companies.Add(c);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = c.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, HullConditionMilli = 100_000, EngineConditionMilli = 100_000 };
            db.AircraftInstances.Add(inst);
            var crew = new Staff { Id = Guid.NewGuid(), CompanyId = c.Id, Name = "Crew", Role = StaffRole.Pilot, SkillMilli = 70_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(crew);
            if (ace is { } role)
                db.Executives.Add(new Executive { Id = Guid.NewGuid(), CompanyId = c.Id, Role = role, Name = "Ace", CompetenceMilli = 100_000, SalaryPerDayCents = 50_000, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow, IsActive = true });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, 1_000_000m, "start");
            cid = c.Id; crewId = crew.Id; aircraftId = inst.Id;
        }
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).CreateStandingOrderAsync(cid, crewId, aircraftId, "EHRD");
        return (cid, aircraftId);
    }

    private static async Task<ReconcileDigest> ReconcileOverTwoDaysAsync(TestDb tdb, FakeClock clock, Guid cid)
    {
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using var db = tdb.NewContext();
        return await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(cid);
    }

    [Fact]
    public async Task Coo_LiftsThroughput()
    {
        async Task<int> TripsAsync(ExecutiveRole? ace)
        {
            using var tdb = new TestDb(); var clock = new FakeClock();
            var (cid, _) = await SeedAsync(tdb, clock, ace);
            return (await ReconcileOverTwoDaysAsync(tdb, clock, cid)).Trips;
        }
        int with = await TripsAsync(ExecutiveRole.ChiefOperating);
        int without = await TripsAsync(null);
        Assert.True(without > 0);
        Assert.True(with > without); // the COO ran more trips through the same tail
    }

    [Fact]
    public async Task Cfo_CutsFuelCost()
    {
        async Task<long> FuelAsync(ExecutiveRole? ace)
        {
            using var tdb = new TestDb(); var clock = new FakeClock();
            var (cid, _) = await SeedAsync(tdb, clock, ace);
            return (await ReconcileOverTwoDaysAsync(tdb, clock, cid)).FuelCents;
        }
        long with = await FuelAsync(ExecutiveRole.ChiefFinancial);
        long without = await FuelAsync(null); // no COO in either → same trips, so fuel differs only by the CFO discount
        Assert.True(without > 0);
        Assert.True(with < without);
    }

    [Fact]
    public async Task MaintenanceDirector_SlowsWear()
    {
        async Task<int> ConditionAsync(ExecutiveRole? ace)
        {
            using var tdb = new TestDb(); var clock = new FakeClock();
            var (cid, aircraftId) = await SeedAsync(tdb, clock, ace);
            await ReconcileOverTwoDaysAsync(tdb, clock, cid);
            using var db = tdb.NewContext();
            return (await db.AircraftInstances.FindAsync(aircraftId))!.HullConditionMilli;
        }
        int with = await ConditionAsync(ExecutiveRole.MaintenanceDirector);
        int without = await ConditionAsync(null);
        Assert.True(without < 100_000);   // the tail wore in
        Assert.True(with > without);      // ...but less, under the Maintenance Director
    }
}
