using Callsign.Core.Airports;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class BaseServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, AirportKind kind)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = 52, Longitude = 4, Kind = kind };

    private static async Task<Guid> SeedAsync(TestDb tdb, IClock clock, long cashCents)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.Airports.Add(A("EHRD", AirportKind.MediumAirport));
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return company.Id;
    }

    private static BaseService NewBaseService(Callsign.Core.Data.CallsignDbContext db, IClock clock)
        => new(db, new LedgerService(db, clock), clock, Cfg, new AirportRepository(db));

    [Fact]
    public async Task OpenBase_BillsSetup_ViaLedger()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000); // $100k

        using (var db = tdb.NewContext())
            await NewBaseService(db, clock).OpenBaseAsync(companyId, "EHRD");

        using (var db = tdb.NewContext())
        {
            var b = await db.Bases.SingleAsync(x => x.AirportIcao == "EHRD");
            Assert.False(b.IsHome);
            Assert.Equal(Cfg.BaseRentPerDayCents(AirportKind.MediumAirport), b.RentPerDayCents);
            var debit = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.BaseRent);
            Assert.Equal(-Cfg.BaseOpenCents(AirportKind.MediumAirport), debit.AmountCents);
            Assert.Equal(b.Id, debit.BaseId);
            Assert.Equal(10_000_000 - Cfg.BaseOpenCents(AirportKind.MediumAirport), (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }

    [Fact]
    public async Task Reconcile_BillsBaseRent_OverElapsedTime()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        using (var db = tdb.NewContext())
            await NewBaseService(db, clock).OpenBaseAsync(companyId, "EHRD");

        clock.UtcNow = clock.UtcNow.AddDays(3);

        using (var db = tdb.NewContext())
        {
            var digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
            Assert.Equal(3 * Cfg.BaseRentPerDayCents(AirportKind.MediumAirport), digest.RentCents);
        }
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.BaseRent && e.Description.StartsWith("Base rent"));
    }

    [Fact]
    public async Task OpenBase_SameIdempotencyKey_BillsOnce_ReturnsSameBase()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);

        Guid first, second;
        using (var db = tdb.NewContext())
            first = (await NewBaseService(db, clock).OpenBaseAsync(companyId, "EHRD", "open-1")).Id;
        using (var db = tdb.NewContext())
            second = (await NewBaseService(db, clock).OpenBaseAsync(companyId, "EHRD", "open-1")).Id; // retry, same token

        Assert.Equal(first, second); // the same base, not a second one (and no "already have a base" error)
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.Bases.CountAsync(b => b.CompanyId == companyId));
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.BaseRent));
            Assert.Equal(10_000_000 - Cfg.BaseOpenCents(AirportKind.MediumAirport), (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }
}
