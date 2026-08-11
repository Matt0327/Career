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

    [Fact]
    public void Pricing_NullSpecs_IsBaseCategoryOnly()
    {
        var t = new AircraftType { Id = Guid.NewGuid(), Key = "X", CanonicalName = "Mystery", Category = AircraftCategory.Turboprop };
        var q = AircraftPricing.Quote(Cfg, t);
        Assert.Single(q.Factors); // just the category base, no spec premiums
        Assert.Equal(Cfg.AircraftBaseCents(AircraftCategory.Turboprop), q.TotalCents);
    }

    [Fact]
    public async Task ConcurrentCashMoves_Conflict_InsteadOfClobbering()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 1_000_000, C172());

        // Two independent contexts both load the company at the same version and stage a debit.
        using var db1 = tdb.NewContext();
        using var db2 = tdb.NewContext();
        await new LedgerService(db1, clock).StageBatchAsync(companyId, new[] { new LedgerPosting(LedgerCategory.Adjustment, -100m, "a") });
        await new LedgerService(db2, clock).StageBatchAsync(companyId, new[] { new LedgerPosting(LedgerCategory.Adjustment, -200m, "b") });

        await db1.SaveChangesAsync(); // first writer wins, bumps the version token
        // The second writer's version is now stale — it conflicts instead of silently clobbering the cache.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Maintain_BillsViaLedger_AndRestoresCondition()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 10_000_000, type); // $100k

        Guid instId;
        using (var db = tdb.NewContext())
        {
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1", LocationIcao = "EHAM",
                AirframeHours = 60, HullConditionMilli = 40_000, EngineConditionMilli = 50_000, MaintenanceHoursWatermark = 0,
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            instId = inst.Id;
        }

        long cost;
        using (var db = tdb.NewContext())
            cost = await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).MaintainAsync(companyId, instId);

        Assert.Equal(Cfg.MaintenanceBaseCents + 60 * Cfg.MaintenancePerHourCents, cost); // base + per-hour since watermark

        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(instId);
            Assert.Equal(100_000, inst!.HullConditionMilli);       // restored
            Assert.Equal(100_000, inst.EngineConditionMilli);
            Assert.Equal(60d, inst.MaintenanceHoursWatermark);     // watermark reset to current hours
            var debit = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.Repair);
            Assert.Equal(-cost, debit.AmountCents);
            Assert.Equal(instId, debit.AircraftInstanceId);
            Assert.Equal(10_000_000 - cost, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }
}
