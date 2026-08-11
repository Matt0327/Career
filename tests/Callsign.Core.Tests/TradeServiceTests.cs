using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class TradeServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static TradeService Trade(CallsignDbContext db, IClock clock)
        => new(db, new LedgerService(db, clock), new MarketService(clock, Cfg), clock, Cfg);

    // Seed a company with cash and (optionally) one owned airframe whose type sets the carry cap.
    private static async Task<Guid> SeedAsync(TestDb tdb, IClock clock, long cashCents, int? usefulLoad = 5000)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Trade Co" };
        db.Companies.Add(company);
        if (usefulLoad is int ul)
        {
            var type = new AircraftType
            {
                Id = Guid.NewGuid(), Key = "PC12", CanonicalName = "Pilatus PC-12",
                Category = AircraftCategory.Turboprop, Seats = 9, UsefulLoadLbs = ul,
            };
            db.AircraftTypes.Add(type);
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-TEST",
                LocationIcao = "EHAM",
            });
        }
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return company.Id;
    }

    [Fact]
    public async Task Buy_SetsCostBasis_DebitsLedger_ReducesCash()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        long buyPrice = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 10);

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(10, lot.Quantity);
            Assert.Equal(buyPrice, lot.UnitCostCents);
            Assert.Equal("EHAM", lot.LocationIcao);

            var company = await db.Companies.FindAsync(companyId);
            Assert.Equal(10_000_000 - buyPrice * 10, company!.CashCents);
            var debit = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.Trade);
            Assert.Equal(-buyPrice * 10, debit.AmountCents);
            Assert.Equal(LedgerRefType.StockLot, debit.RelatedEntityType);
        }
    }

    [Fact]
    public async Task Buy_Again_BlendsCostBasis_WeightedAverage()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var companyId = await SeedAsync(tdb, clock, 100_000_000);
        var market = new MarketService(clock, Cfg);
        long p1 = market.Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 4);

        clock.UtcNow = clock.UtcNow.AddHours(12); // prices re-roll to a new window
        long p2 = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 6);

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(10, lot.Quantity);
            Assert.Equal((p1 * 4 + p2 * 6) / 10, lot.UnitCostCents); // weighted average, integer cents
        }
    }

    [Fact]
    public async Task Sell_CreditsProceeds_RealisesPnl_ReducesQuantity()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        var market = new MarketService(clock, Cfg);
        var coffee = TradeCatalog.Find("coffee")!;
        long buy = market.Quote("EHAM", coffee).BuyCents;
        long sell = market.Quote("EHAM", coffee).SellCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 10);

        TradeResult r;
        using (var db = tdb.NewContext())
            r = await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 4);

        Assert.Equal(4, r.Quantity);
        Assert.Equal(sell * 4, r.ProceedsCents);
        Assert.Equal(buy * 4, r.CostBasisCents);
        Assert.Equal((sell - buy) * 4, r.PnlCents); // negative: same-airport round trip loses the spread

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(6, lot.Quantity);
            long expectedCash = 10_000_000 - buy * 10 + sell * 4;
            Assert.Equal(expectedCash, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }

    [Fact]
    public async Task Sell_Elsewhere_CanRealiseProfit()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000);
        var market = new MarketService(clock, Cfg);
        var coffee = TradeCatalog.Find("coffee")!;

        // Buy where it's cheapest, sell where it's dearest — the swing across the map makes this pay.
        string[] cities = ["EHAM", "EGLL", "KJFK", "LFPG", "EDDF", "LEMD", "LIRF", "EIDW", "KLAX", "KORD", "YSSY", "RJTT", "OMDB", "VHHH", "KSFO", "KMIA"];
        string buyAt = cities.OrderBy(i => market.Quote(i, coffee).BuyCents).First();
        string sellAt = cities.OrderByDescending(i => market.Quote(i, coffee).SellCents).First();
        long buyHere = market.Quote(buyAt, coffee).BuyCents;
        long sellThere = market.Quote(sellAt, coffee).SellCents;
        Assert.NotEqual(buyAt, sellAt);
        Assert.True(sellThere > buyHere); // arbitrage exists across the set

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, buyAt, "coffee", 10);

        // Simulate flying the goods to the destination (settlement does this on a real leg).
        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            lot.LocationIcao = sellAt;
            await db.SaveChangesAsync();
        }

        TradeResult r;
        using (var db = tdb.NewContext())
            r = await Trade(db, clock).SellAsync(companyId, sellAt, "coffee", 10);

        Assert.Equal((sellThere - buyHere) * 10, r.PnlCents);
        Assert.True(r.PnlCents > 0); // bought low, sold high
    }

    [Fact]
    public async Task Buy_TooExpensive_Throws_AndMovesNoMoney()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 1_000); // $10, nowhere near enough

        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Trade(db, clock).BuyAsync(companyId, "EHAM", "machinery", 5));

        using (var db = tdb.NewContext())
        {
            Assert.Empty(await db.InventoryLots.ToListAsync());
            Assert.Equal(1_000, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }

    [Fact]
    public async Task Buy_OverCarryCapacity_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        // No aircraft → capacity falls back to the default (1500 lb). Machinery is 120 lb/unit.
        var companyId = await SeedAsync(tdb, clock, 100_000_000, usefulLoad: null);

        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Trade(db, clock).BuyAsync(companyId, "EHAM", "machinery", 20)); // 2400 lb > 1500
    }

    [Fact]
    public async Task Sell_MoreThanHeld_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 3);
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 5));
    }

    [Fact]
    public async Task Sell_WhereYouHoldNothing_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 1));
    }

    [Fact]
    public async Task Buy_SameIdempotencyKey_ChargesOnce()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        long buyPrice = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;

        Guid first, second;
        using (var db = tdb.NewContext())
            first = (await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 5, "trade-1")).Id;
        using (var db = tdb.NewContext())
            second = (await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 5, "trade-1")).Id; // retry, same token

        Assert.Equal(first, second);
        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(5, lot.Quantity); // bought once, not 10
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.Trade));
            Assert.Equal(10_000_000 - buyPrice * 5, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }
}
