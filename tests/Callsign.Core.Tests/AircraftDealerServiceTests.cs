using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class AircraftDealerServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static AircraftType C172() => new()
    {
        Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172 Skyhawk",
        Category = AircraftCategory.LightSingle, Seats = 4, UsefulLoadLbs = 900, CruiseKtas = 120,
    };

    private static async Task<Guid> SeedCompanyWithCashAsync(TestDb tdb, IClock clock, long cashCents, AircraftType type)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.AircraftTypes.Add(type);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return company.Id;
    }

    [Fact]
    public async Task Buy_CreatesOwnedAirframe_AndDebitsViaLedger()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var quote = AircraftPricing.Quote(Cfg, type);
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, quote.TotalCents + 1_000_000, type);

        Guid boughtId;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            boughtId = (await dealer.BuyAsync(companyId, type.Id, "EHAM")).Id;
        }

        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(boughtId);
            Assert.NotNull(inst);
            Assert.Equal(companyId, inst!.CompanyId);
            Assert.Equal("EHAM", inst.LocationIcao);
            Assert.Equal(OwnershipKind.Owned, inst.Ownership);
            Assert.Equal(quote.TotalCents, inst.PurchasePriceCents);

            var company = await db.Companies.FindAsync(companyId);
            Assert.Equal(1_000_000, company!.CashCents); // starting - price

            var debit = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.AircraftPurchase);
            Assert.Equal(-quote.TotalCents, debit.AmountCents);
            Assert.Equal(boughtId, debit.AircraftInstanceId); // attributed to the airframe

            // cash still equals the ledger sum (the core invariant)
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(company.CashCents, ledgerSum);
        }
    }

    [Fact]
    public async Task Buy_WithoutEnoughCash_Throws_AndBuysNothing()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 10_000, type); // $100 — nowhere near

        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            await Assert.ThrowsAsync<InvalidOperationException>(() => dealer.BuyAsync(companyId, type.Id, "EHAM"));
        }

        using (var db = tdb.NewContext())
        {
            Assert.Empty(await db.AircraftInstances.ToListAsync());
            Assert.Equal(10_000, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }
}
