using Callsign.Core.Airports;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 11e — the hub network. Promoting a base to a hub is a capex facility (like the 7g shop/fuel
/// farm) that AMPLIFIES the 11c operating-reputation demand lift at that field, scaled by hub level. Level 0
/// (a plain base) is byte-identical to 11c.</summary>
public class HubNetworkTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon, AirportKind kind = AirportKind.LargeAirport)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = kind };

    [Fact]
    public void HubLevel_AmplifiesPayFactorAndOfferCount()
    {
        Assert.Equal(1.15, Cfg.HubReputationPayFactor(100_000, 0), 6);                        // level 0 = 11c exactly
        Assert.Equal(1.0 + 0.15 * (1 + 3 * 0.5), Cfg.HubReputationPayFactor(100_000, 3), 6);  // L3 amplifies the swing 2.5× → 1.375
        Assert.Equal(1.0, Cfg.HubReputationPayFactor(0, 3));                                  // rep 0 → no lift, whatever the hub level
        Assert.Equal(12, Cfg.HubReputationOfferCount(8, 100_000, 0));                         // 11c board widening
        Assert.Equal(18, Cfg.HubReputationOfferCount(8, 100_000, 3));                         // 8 × (1 + 0.5×2.5) = 8 × 2.25
    }

    private static BaseService Bases(Callsign.Core.Data.CallsignDbContext db, IClock clock)
        => new(db, new LedgerService(db, clock), clock, Cfg, new AirportRepository(db));

    private static async Task<Guid> SeedCashCompanyAsync(TestDb tdb, IClock clock, long cashCents)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.Airports.Add(A("EHRD", 51.95, 4.44, AirportKind.MediumAirport));
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return company.Id;
    }

    [Fact]
    public async Task UpgradeHub_RaisesLevel_BillsCapex_AndReconciles()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCashCompanyAsync(tdb, clock, 100_000_000); // $1M
        Guid baseId;
        using (var db = tdb.NewContext())
            baseId = (await Bases(db, clock).OpenBaseAsync(companyId, "EHRD")).Id;

        int level;
        using (var db = tdb.NewContext())
            level = await Bases(db, clock).UpgradeHubAsync(companyId, baseId);

        Assert.Equal(1, level);
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, (await db.Bases.FindAsync(baseId))!.HubLevel);
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.AmountCents == -Cfg.HubUpgradeCents(1) && e.BaseId == baseId);
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // the capex reconciles through the ledger
        }

        // Idempotent under the same key: the keyed L1→L2 upgrade applies once; a retry with the same key is a no-op.
        int keyed1, keyed2;
        using (var db = tdb.NewContext()) keyed1 = await Bases(db, clock).UpgradeHubAsync(companyId, baseId, "hub-1");
        using (var db = tdb.NewContext()) keyed2 = await Bases(db, clock).UpgradeHubAsync(companyId, baseId, "hub-1");
        Assert.Equal(2, keyed1);
        Assert.Equal(2, keyed2); // retry returns the same level, no second charge
        using (var db = tdb.NewContext())
            Assert.Single(await db.LedgerEntries.Where(e => e.Description.Contains("Hub L2")).ToListAsync()); // exactly one L2 charge
    }

    [Fact]
    public async Task UpgradeHub_CapsAtMaxLevel()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCashCompanyAsync(tdb, clock, 2_000_000_000); // plenty
        Guid baseId;
        using (var db = tdb.NewContext())
            baseId = (await Bases(db, clock).OpenBaseAsync(companyId, "EHRD")).Id;

        for (int i = 0; i < Cfg.MaxHubLevel; i++)
            using (var db = tdb.NewContext())
                await Bases(db, clock).UpgradeHubAsync(companyId, baseId);

        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Bases(db, clock).UpgradeHubAsync(companyId, baseId));
    }

    [Fact]
    public async Task Route_AtUpgradedHub_PaysMoreThanPlainBase()
    {
        static async Task<long> RouteRewardAtHubLevel(int hubLevel)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            Guid companyId, aircraftId, staffId;
            using (var db = tdb.NewContext())
            {
                var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = 100_000 };
                db.Companies.Add(company);
                db.Airports.Add(A("EHAM", 52.31, 4.76));
                db.Airports.Add(A("EHRD", 51.95, 4.44, AirportKind.MediumAirport));
                db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true, HubLevel = hubLevel });
                db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true });
                var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
                var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
                var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "A", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 60_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
                db.AddRange(type, inst, staff);
                await db.SaveChangesAsync();
                companyId = company.Id; aircraftId = inst.Id; staffId = staff.Id;
            }
            using var db2 = tdb.NewContext();
            return (await new RouteService(db2, clock, Cfg).CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo)).RewardPerTripCents;
        }

        long plainBase = await RouteRewardAtHubLevel(0);
        long maxHub = await RouteRewardAtHubLevel(3);
        Assert.True(maxHub > plainBase); // the upgraded hub amplifies the reputation lift baked into the route's frozen pay
    }

    [Fact]
    public async Task Reconcile_BillsHubUpkeep()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.Add(A("EHAM", 52.31, 4.76));
            // A rent-free base so the ONLY base charge is the hub upkeep — proves the hub's running cost is billed.
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsActive = true, RentPerDayCents = 0, HubLevel = 3, LastRentBilledAt = clock.UtcNow });
            await db.SaveChangesAsync();
            companyId = company.Id;
        }

        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            long expected = (long)Math.Round(2.0 * Cfg.HubUpkeepCentsPerDay(3)); // 2 days of L3 hub upkeep
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.AmountCents == -expected && e.Description.StartsWith("Base costs"));
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents);
        }
    }
}
