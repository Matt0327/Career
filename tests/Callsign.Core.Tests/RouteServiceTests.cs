using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class RouteServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static RouteService Routes(CallsignDbContext db, IClock clock) => new(db, clock, Cfg);
    private static OperationsService Ops(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    private static async Task<(Guid CompanyId, Guid AircraftId, Guid StaffId)> SeedAsync(TestDb tdb, IClock clock, bool bothBases = true)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.Airports.Add(new Airport { Ident = "EHAM", IcaoCode = "EHAM", Name = "Schiphol", Latitude = 52.31, Longitude = 4.76, Kind = AirportKind.LargeAirport });
        db.Airports.Add(new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = 51.95, Longitude = 4.44, Kind = AirportKind.MediumAirport });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
        if (bothBases)
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true });
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Availability = AircraftAvailability.Available };
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 60_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.AddRange(type, inst, staff);
        await db.SaveChangesAsync();
        return (company.Id, inst.Id, staff.Id);
    }

    [Fact]
    public async Task Create_BetweenBases_FreezesReward_AndReservesAircraft()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
        {
            var r = await Routes(db, clock).CreateRouteAsync(companyId, "NL Freight", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);
            Assert.True(r.RewardPerTripCents > 0);
            Assert.True(r.DistanceNm > 0 && r.RoundTripHours > 0);
            Assert.Equal(MissionType.Cargo, r.Mission);
        }
        using (var db = tdb.NewContext())
            Assert.Equal(AircraftAvailability.Reserved, (await db.AircraftInstances.FindAsync(aircraftId))!.Availability);
    }

    [Fact]
    public async Task Create_NonBaseEndpoint_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, bothBases: false); // EHRD isn't a base
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo));
    }

    [Fact]
    public async Task Create_ReputationOrIllicitMission_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Illicit));
    }

    [Fact]
    public async Task Reconcile_BooksRouteTrips_FeeFree()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Routes(db, clock).CreateRouteAsync(companyId, "NL Freight", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);

        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
        {
            var digest = await Ops(db, clock).ReconcileAsync(companyId);
            Assert.True(digest.Trips > 0);
            Assert.True(digest.GrossIncomeCents > 0);
            Assert.Equal(0, digest.FeesCents); // both ends are your bases — no landing fees
        }
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Description.StartsWith("Route"));
    }

    [Fact]
    public async Task Cancel_FreesAircraft()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        Guid routeId;
        using (var db = tdb.NewContext())
            routeId = (await Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo)).Id;
        using (var db = tdb.NewContext())
            await Routes(db, clock).CancelRouteAsync(companyId, routeId);
        using (var db = tdb.NewContext())
            Assert.Equal(AircraftAvailability.Available, (await db.AircraftInstances.FindAsync(aircraftId))!.Availability);
    }
}
