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

    // --- Airworthiness & inspections (Phase 7e) ---

    private static AircraftInstance Airframe(FakeClock clock, double hours = 0, int hull = 100_000, int engine = 100_000,
        double last100h = 0, DateTimeOffset? lastAnnual = null, DateTimeOffset? acquired = null)
        => new()
        {
            Id = Guid.NewGuid(), TypeId = Guid.NewGuid(), CompanyId = Guid.NewGuid(), Tail = "CS-1",
            LocationIcao = "EHAM", AirframeHours = hours, HullConditionMilli = hull, EngineConditionMilli = engine,
            Last100hHoursWatermark = last100h, LastAnnualAt = lastAnnual, AcquiredAt = acquired ?? clock.UtcNow,
        };

    private static AircraftDealerService Dealer(TestDb tdb, FakeClock clock)
    {
        var db = tdb.NewContext();
        return new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
    }

    [Fact]
    public void Airworthiness_FreshAirframe_IsAirworthy()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Assert.True(Dealer(tdb, clock).Airworthiness(Airframe(clock)).Airworthy);
    }

    [Fact]
    public void Airworthiness_Over100Hours_IsGrounded()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var aw = Dealer(tdb, clock).Airworthiness(Airframe(clock, hours: 100, last100h: 0));
        Assert.False(aw.Airworthy);
        Assert.Contains("100-hour", aw.Reason);
    }

    [Fact]
    public void Airworthiness_AnnualOverdue_IsGrounded()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var aw = Dealer(tdb, clock).Airworthiness(Airframe(clock, acquired: clock.UtcNow.AddDays(-400)));
        Assert.False(aw.Airworthy);
        Assert.Contains("annual", aw.Reason);
    }

    [Fact]
    public void Airworthiness_BelowConditionFloor_IsGrounded()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var aw = Dealer(tdb, clock).Airworthiness(Airframe(clock, hull: 15_000));
        Assert.False(aw.Airworthy);
        Assert.Contains("condition", aw.Reason);
    }

    [Fact]
    public async Task Inspect_Clears100Hour_ReturnsToService_AndBills()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 5_000_000, type);
        Guid instId;
        using (var db = tdb.NewContext())
        {
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM",
                AirframeHours = 100, AcquiredAt = clock.UtcNow, // 100h due, annual fresh
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            instId = inst.Id;
        }

        long cost;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            Assert.False(dealer.Airworthiness((await db.AircraftInstances.FindAsync(instId))!).Airworthy); // grounded before
            cost = await dealer.InspectAsync(companyId, instId);
        }
        Assert.Equal(Cfg.HundredHourInspectionCents, cost);

        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(instId);
            Assert.Equal(100, inst!.Last100hHoursWatermark); // watermark advanced
            Assert.True(new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).Airworthiness(inst).Airworthy); // back in service
            Assert.Contains(await db.LedgerEntries.ToListAsync(),
                e => e.Category == LedgerCategory.Repair && e.AmountCents == -Cfg.HundredHourInspectionCents);
        }
    }

    [Fact]
    public async Task Maintain_AtABaseWithAShop_IsDiscounted()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 100_000_000, type);
        Guid instId;
        using (var db = tdb.NewContext())
        {
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1", LocationIcao = "EHAM",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, AirframeHours = 60,
            };
            db.AircraftInstances.Add(inst);
            db.Bases.Add(new Base
            {
                Id = Guid.NewGuid(), CompanyId = companyId, AirportIcao = "EHAM", RentPerDayCents = 0,
                MaintenanceLevel = 1, OpenedAt = clock.UtcNow, LastRentBilledAt = clock.UtcNow, IsActive = true,
            });
            await db.SaveChangesAsync();
            instId = inst.Id;
        }

        long full, charged;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            full = dealer.MaintenanceQuoteCents((await db.AircraftInstances.FindAsync(instId))!); // pre-discount quote
            charged = await dealer.MaintainAsync(companyId, instId);
        }
        Assert.True(charged < full);
        Assert.Equal((long)Math.Round(full * (1 - Cfg.MaintenanceShopDiscountPct(1))), charged);
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

        long cost, expected;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            var inst = await db.AircraftInstances.FindAsync(instId);
            // Phase 13 — damage-scaled: base + per-hour since watermark, PLUS a wear charge for the worn hull + engine.
            expected = dealer.MaintenanceQuoteCents(inst!, MaintenanceScope.Both, dealer.MarketValueCents(type));
            cost = await dealer.MaintainAsync(companyId, instId);
        }
        Assert.True(cost > Cfg.MaintenanceBaseCents + 60 * Cfg.MaintenancePerHourCents); // damage adds to the flat visit
        Assert.Equal(expected, cost);

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

    [Fact]
    public async Task Maintain_EngineOnly_RestoresEngine_LeavesHullWorn_AndCostsLess()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 100_000_000, type);
        Guid instId;
        using (var db = tdb.NewContext())
        {
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1", LocationIcao = "EHAM",
                Ownership = OwnershipKind.Owned, AirframeHours = 60, HullConditionMilli = 40_000, EngineConditionMilli = 50_000,
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            instId = inst.Id;
        }

        long engineOnly, both;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            var inst = await db.AircraftInstances.FindAsync(instId);
            both = dealer.MaintenanceQuoteCents(inst!, MaintenanceScope.Both, dealer.MarketValueCents(type));
            engineOnly = await dealer.MaintainAsync(companyId, instId, MaintenanceScope.Engine);
        }
        Assert.True(engineOnly < both); // servicing one component is cheaper than both
        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(instId);
            Assert.Equal(100_000, inst!.EngineConditionMilli); // engine restored
            Assert.Equal(40_000, inst.HullConditionMilli);     // hull left as it was
        }
    }

    [Fact]
    public async Task Buy_SameIdempotencyKey_ChargesOnce_ReturnsSameAirframe()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var quote = AircraftPricing.Quote(Cfg, type);
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, quote.TotalCents * 3, type);

        Guid first, second;
        using (var db = tdb.NewContext())
            first = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg)
                .BuyAsync(companyId, type.Id, "EHAM", "buy-token-1")).Id;
        using (var db = tdb.NewContext())
            second = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg)
                .BuyAsync(companyId, type.Id, "EHAM", "buy-token-1")).Id; // retried request, same token

        Assert.Equal(first, second); // the same airframe — not a second purchase
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.AircraftInstances.CountAsync(a => a.CompanyId == companyId));
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.AircraftPurchase));
            Assert.Equal(quote.TotalCents * 3 - quote.TotalCents, (await db.Companies.FindAsync(companyId))!.CashCents); // charged once
        }
    }

    [Fact]
    public async Task Maintain_SameIdempotencyKey_BillsOnce()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 10_000_000, type);
        Guid instId;
        using (var db = tdb.NewContext())
        {
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1", LocationIcao = "EHAM",
                AirframeHours = 60, MaintenanceHoursWatermark = 0,
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            instId = inst.Id;
        }

        long c1, c2;
        using (var db = tdb.NewContext())
            c1 = await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).MaintainAsync(companyId, instId, MaintenanceScope.Both, "maint-1");
        using (var db = tdb.NewContext())
            c2 = await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).MaintainAsync(companyId, instId, MaintenanceScope.Both, "maint-1");

        Assert.Equal(c1, c2);
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.Repair));
            Assert.Equal(10_000_000 - c1, (await db.Companies.FindAsync(companyId))!.CashCents); // billed once
        }
    }

    // --- Used-aircraft market (Phase 7g) ---

    [Fact]
    public async Task UsedMarket_IsDeterministic_AndPricedBelowNew()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        await SeedCompanyWithCashAsync(tdb, clock, 0, type);
        using var db = tdb.NewContext();
        var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);

        var a = await dealer.GetUsedMarketAsync(1234);
        var b = await dealer.GetUsedMarketAsync(1234);
        Assert.Equal(Cfg.UsedMarketCount, a.Count);
        Assert.Equal(a, b); // same seed → the same slate (record value-equality)
        Assert.All(a, l =>
        {
            Assert.True(l.PriceCents < l.NewPriceCents);      // always cheaper than new
            Assert.InRange(l.ConditionMilli, 50_000, 95_000); // worn, but airworthy
            Assert.InRange(l.AirframeHours, 200, 3_000);
        });
    }

    [Fact]
    public async Task BuyUsed_CreatesWornInstance_AtUsedPrice_DebitsLedger()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 1_000_000_000, type); // $10M

        UsedListing listing;
        using (var db = tdb.NewContext())
            listing = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).GetUsedMarketAsync(77))[0];

        Guid boughtId;
        using (var db = tdb.NewContext())
            boughtId = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg)
                .BuyUsedAsync(companyId, listing.TypeId, listing.Seed, "EHAM")).Id;

        using (var db = tdb.NewContext())
        {
            var inst = await db.AircraftInstances.FindAsync(boughtId);
            Assert.Equal(OwnershipKind.Owned, inst!.Ownership);
            Assert.Equal(listing.ConditionMilli, inst.HullConditionMilli);   // arrives worn, as listed
            Assert.Equal(listing.ConditionMilli, inst.EngineConditionMilli);
            Assert.Equal(listing.AirframeHours, inst.AirframeHours);
            Assert.True(inst.HullConditionMilli < 100_000);                  // not pristine
            Assert.Equal(listing.PriceCents, inst.PurchasePriceCents);

            var company = await db.Companies.FindAsync(companyId);
            Assert.Equal(1_000_000_000 - listing.PriceCents, company!.CashCents);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(company.CashCents, ledgerSum); // the core invariant
            Assert.Contains(await db.LedgerEntries.ToListAsync(),
                e => e.Category == LedgerCategory.AircraftPurchase && e.AmountCents == -listing.PriceCents && e.AircraftInstanceId == boughtId);
        }
    }

    [Fact]
    public async Task BuyUsed_SameIdempotencyKey_BuysOnce()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 1_000_000_000, type);

        UsedListing l;
        using (var db = tdb.NewContext())
            l = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).GetUsedMarketAsync(9))[0];

        Guid first, second;
        using (var db = tdb.NewContext())
            first = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).BuyUsedAsync(companyId, l.TypeId, l.Seed, "EHAM", "used-1")).Id;
        using (var db = tdb.NewContext())
            second = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).BuyUsedAsync(companyId, l.TypeId, l.Seed, "EHAM", "used-1")).Id;

        Assert.Equal(first, second); // retry replays the same airframe
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.AircraftInstances.CountAsync(a => a.CompanyId == companyId));
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.AircraftPurchase));
        }
    }

    [Fact]
    public async Task BuyUsed_Maintain_Resell_IsAlwaysALoss_NoFlipPump()
    {
        // Regression guard (found by adversarial review): the flip is worst for the LOWEST-condition listing —
        // cheapest to buy, most condition to restore. Buying it, restoring to 100% via a flat-fee service, then
        // reselling must STILL lose: the used floor sits above the post-restore resale value, for any aircraft.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var type = C172();
        var companyId = await SeedCompanyWithCashAsync(tdb, clock, 1_000_000_000, type);

        UsedListing worst;
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            var all = new List<UsedListing>();
            for (int seed = 0; seed < 30; seed++)
                all.AddRange(await dealer.GetUsedMarketAsync(seed));
            worst = all.OrderBy(l => l.ConditionMilli).First(); // the most exploitable listing
        }

        long cashStart;
        using (var db = tdb.NewContext())
            cashStart = (await db.Companies.FindAsync(companyId))!.CashCents;

        Guid id;
        using (var db = tdb.NewContext())
            id = (await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg)
                .BuyUsedAsync(companyId, worst.TypeId, worst.Seed, "EHAM")).Id;
        using (var db = tdb.NewContext())
            await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).MaintainAsync(companyId, id); // restore to 100%
        using (var db = tdb.NewContext())
            await new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg).SellAsync(companyId, id);

        using (var db = tdb.NewContext())
            Assert.True((await db.Companies.FindAsync(companyId))!.CashCents < cashStart); // buy + maintain + sell nets a loss
    }
}
