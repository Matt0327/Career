using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class TradeServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static TradeService Trade(CallsignDbContext db, IClock clock)
        => new(db, new LedgerService(db, clock), new MarketService(clock, Cfg), clock, Cfg, new WorldOracle(Cfg));

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
    public async Task BestNearbySells_FindsAFieldForEachGood_FromNearbySuitableAirports_NeverTheOrigin()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(
                new Airport { Ident = "EHAM", IcaoCode = "EHAM", Name = "Schiphol", Latitude = 52.3086, Longitude = 4.7639, Kind = AirportKind.LargeAirport, LongestRunwayFt = 11_000 },
                new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = 51.9569, Longitude = 4.4372, Kind = AirportKind.LargeAirport, LongestRunwayFt = 8_000 },
                new Airport { Ident = "EHEH", IcaoCode = "EHEH", Name = "Eindhoven", Latitude = 51.4501, Longitude = 5.3745, Kind = AirportKind.LargeAirport, LongestRunwayFt = 9_000 });
            await db.SaveChangesAsync();
        }
        using var db2 = tdb.NewContext();
        var best = await Trade(db2, clock).BestNearbySellsAsync("EHAM");
        Assert.Equal(TradeCatalog.Goods.Count, best.Count); // a best-sell for every commodity
        Assert.All(best.Values, b =>
        {
            Assert.NotEqual("EHAM", b.Icao);                       // the point is to sell high SOMEWHERE ELSE
            Assert.Contains(b.Icao, new[] { "EHRD", "EHEH" });
            Assert.True(b.SellCents > 0 && b.DistanceNm > 0);
        });
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
        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 4);

        clock.UtcNow = clock.UtcNow.AddHours(12); // prices re-roll to a new window

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 6);

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            // Each buy was charged whatever the market asked (the first also nudged the local price up for
            // the second); the cost basis is their weighted average. Recover each actual price from its
            // ledger posting so the assertion stays exact regardless of the pressure lift.
            var buys = await db.LedgerEntries
                .Where(e => e.Category == LedgerCategory.Trade).OrderBy(e => e.At).ToListAsync();
            long p1 = -buys[0].AmountCents / 4;
            long p2 = -buys[1].AmountCents / 6;
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
        long buy = market.Quote("EHAM", coffee).BuyCents;          // first buy is at the untouched market
        long sellNeutral = market.Quote("EHAM", coffee).SellCents; // before any pressure

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 10);

        TradeResult r;
        using (var db = tdb.NewContext())
            r = await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 4);

        Assert.Equal(4, r.Quantity);
        Assert.Equal(buy * 4, r.CostBasisCents);                   // cost basis from the neutral first buy
        Assert.True(r.ProceedsCents > sellNeutral * 4);            // buying 10 bid the local sell price up
        Assert.Equal(r.ProceedsCents - r.CostBasisCents, r.PnlCents);
        Assert.True(r.PnlCents < 0);                               // same-airport round trip still loses

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(6, lot.Quantity);
            long expectedCash = 10_000_000 - buy * 10 + r.ProceedsCents; // proceeds are pressure-lifted
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

    [Fact]
    public async Task Sell_SameIdempotencyKey_CreditsOnce_ReplaysExactResult()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 5);

        TradeResult r1, r2;
        using (var db = tdb.NewContext())
            r1 = await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 3, "sell-1");
        using (var db = tdb.NewContext())
            r2 = await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 3, "sell-1"); // retry, same token

        Assert.True(r1.ProceedsCents > 0);
        Assert.Equal(r1.Quantity, r2.Quantity);          // replay reconstructs the same breakdown
        Assert.Equal(r1.ProceedsCents, r2.ProceedsCents);
        Assert.Equal(r1.CostBasisCents, r2.CostBasisCents);
        Assert.Equal(r1.PnlCents, r2.PnlCents);

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(2, lot.Quantity); // sold 3 once (5 -> 2), not twice
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.Trade && e.AmountCents > 0)); // one credit
        }
    }

    // ── Market pressure (Phase 7g): your own trading moves the local price ──────

    [Fact]
    public async Task Buying_LiftsLocalBuyPrice()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000);
        long neutralBuy = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 60); // ~3600 lb — a real dent in supply

        using (var db = tdb.NewContext())
        {
            var q = (await Trade(db, clock).GetMarketAsync(companyId, "EHAM")).Single(m => m.Good == "coffee");
            Assert.True(q.BuyCents > neutralBuy); // you bid the local market up
            Assert.True(q.PressurePct > 0);
        }
    }

    [Fact]
    public async Task Selling_SoftensLocalSellPrice()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000);
        long neutralSell = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).SellCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "KJFK", "coffee", 60); // buy it somewhere else
        using (var db = tdb.NewContext())                                     // fly the goods to EHAM
        {
            var lot = await db.InventoryLots.SingleAsync(l => l.CompanyId == companyId);
            lot.LocationIcao = "EHAM";
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
            await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 40); // dump ~2400 lb into EHAM

        using (var db = tdb.NewContext())
        {
            var q = (await Trade(db, clock).GetMarketAsync(companyId, "EHAM")).Single(m => m.Good == "coffee");
            Assert.True(q.SellCents < neutralSell); // you flooded the local market
            Assert.True(q.PressurePct < 0);
        }
    }

    [Fact]
    public async Task Pressure_DecaysBackToNeutral()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var companyId = await SeedAsync(tdb, clock, 100_000_000);

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 60);

        using (var db = tdb.NewContext()) // right after buying, the price is lifted
            Assert.True((await Trade(db, clock).GetMarketAsync(companyId, "EHAM"))
                .Single(m => m.Good == "coffee").PressurePct > 0);

        clock.UtcNow = clock.UtcNow.AddDays(30); // leave it alone for ~15 half-lives (2-day HL)

        using (var db = tdb.NewContext())
        {
            var q = (await Trade(db, clock).GetMarketAsync(companyId, "EHAM")).Single(m => m.Good == "coffee");
            long neutral = new MarketService(clock, Cfg).Quote("EHAM", TradeCatalog.Find("coffee")!).BuyCents;
            Assert.Equal(0, q.PressurePct);   // drifted back to neutral
            Assert.Equal(neutral, q.BuyCents);
        }
    }

    [Fact]
    public async Task Buy_Idempotent_MovesLocalPriceOnce()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000);

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 20, "buy-1");
        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 20, "buy-1"); // retry, same token

        using (var db = tdb.NewContext())
        {
            var row = await db.MarketPressures.SingleAsync(
                p => p.CompanyId == companyId && p.Icao == "EHAM" && p.Good == "coffee");
            Assert.Equal(20L * 60, row.PressureLbs); // applied once (1200 lb), not twice
        }
    }

    [Fact]
    public void DecayPressureLbs_HalvesEachHalfLife()
    {
        var hl = TimeSpan.FromDays(2);
        Assert.Equal(1000, MarketService.DecayPressureLbs(1000, TimeSpan.Zero, hl));
        Assert.Equal(500, MarketService.DecayPressureLbs(1000, hl, hl));
        Assert.Equal(250, MarketService.DecayPressureLbs(1000, hl + hl, hl));
        Assert.Equal(-500, MarketService.DecayPressureLbs(-1000, hl, hl)); // symmetric for sell pressure
    }

    [Fact]
    public async Task LargeSameAirportRoundTrip_StillLoses()
    {
        // Regression guard: buying then immediately selling the same goods at the same airport must LOSE the
        // dealer spread, even at a size large enough to fully move the local price. Market pressure must never
        // let you pump your own sell-back above what you paid (MarketPressureSwing <= 2 * TradeSpreadPct).
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000, usefulLoad: 20_000);

        long cashBefore;
        using (var db = tdb.NewContext())
            cashBefore = (await db.Companies.FindAsync(companyId))!.CashCents;

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 200);  // 12,000 lb → full-scale pressure
        using (var db = tdb.NewContext())
            await Trade(db, clock).SellAsync(companyId, "EHAM", "coffee", 200); // sell it all straight back, same tick

        using (var db = tdb.NewContext())
        {
            long cashAfter = (await db.Companies.FindAsync(companyId))!.CashCents;
            Assert.True(cashAfter < cashBefore); // the round trip lost money — no money pump
        }
    }

    [Fact]
    public async Task PressuredBuyPrice_MatchesPureQuoteOracle()
    {
        // Pin the exact MAGNITUDE of the lift against the pure pricing function, not just its direction.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 100_000_000);

        using (var db = tdb.NewContext())
            await Trade(db, clock).BuyAsync(companyId, "EHAM", "coffee", 50); // 3,000 lb, same tick → no decay

        using (var db = tdb.NewContext())
        {
            var q = (await Trade(db, clock).GetMarketAsync(companyId, "EHAM")).Single(m => m.Good == "coffee");
            long expected = new MarketService(clock, Cfg)
                .Quote("EHAM", TradeCatalog.Find("coffee")!, 50L * 60).BuyCents; // oracle at +3,000 lb pressure
            Assert.Equal(expected, q.BuyCents);
        }
    }

    // ── Phase 8f: weather → market demand ────────────────────────────────────────────────────────

    // Find a field whose synthetic weather is foul (low visibility) at time t — deterministic, so stable.
    private static (double lat, double lon, double visSm) FoulField(WorldOracle world, DateTimeOffset t)
    {
        for (double lat = 20; lat < 70; lat += 0.7)
        {
            double vis = world.WeatherAt(lat, 5.0, t).VisibilitySm;
            if (vis < 2.0) return (lat, 5.0, vis);
        }
        throw new Xunit.Sdk.XunitException("no foul-weather cell found in the scan range");
    }

    [Fact]
    public async Task Market_WithoutAnAirport_IsWeatherNeutral()
    {
        // Isolation: an icao with no Airport row (no geography) prices exactly as the plain market — the
        // weather factor stays 1.0. This is why existing trade tests (which seed no airports) don't churn.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedAsync(tdb, clock, 10_000_000);

        using var db = tdb.NewContext();
        var market = await Trade(db, clock).GetMarketAsync(companyId, "NOAP");
        Assert.All(market, q => Assert.Equal(0, q.WeatherPct));
        var g = TradeCatalog.Goods[0];
        var plain = new MarketService(clock, Cfg).Quote("NOAP", g);
        Assert.Equal(plain.SellCents, market.Single(q => q.Good == g.Key).SellCents);
    }

    [Fact]
    public async Task Market_AtAFoulWeatherField_LiftsPrices_AndMatchesTheFormula()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var world = new WorldOracle(Cfg);
        var (lat, lon, visSm) = FoulField(world, clock.UtcNow);
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        using (var db = tdb.NewContext())
        {
            db.Airports.Add(new Airport { Ident = "FOUL", IcaoCode = "FOUL", Name = "Foul Field", Latitude = lat, Longitude = lon, Kind = AirportKind.MediumAirport });
            await db.SaveChangesAsync();
        }

        using (var db = tdb.NewContext())
        {
            var g = TradeCatalog.Goods[0];
            var q = (await Trade(db, clock).GetMarketAsync(companyId, "FOUL")).Single(m => m.Good == g.Key);
            // Same icao, weather off, isolates the lift: FOUL's foul weather sells dearer than its own clear price.
            var neutral = new MarketService(clock, Cfg).Quote("FOUL", g);
            Assert.True(q.WeatherPct > 0);
            Assert.True(q.SellCents > neutral.SellCents);
            // And it equals the pricing oracle at exactly this field's weather factor.
            var expected = new MarketService(clock, Cfg).Quote("FOUL", g, 0, Cfg.WeatherDemandFactor(visSm));
            Assert.Equal(expected.SellCents, q.SellCents);
        }
    }

    [Fact]
    public async Task Sell_AtAFoulField_RealisesTheWeatherLiftedPrice_DisplayMatchesSettlement()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var world = new WorldOracle(Cfg);
        var (lat, lon, visSm) = FoulField(world, clock.UtcNow);
        var companyId = await SeedAsync(tdb, clock, 10_000_000);
        using (var db = tdb.NewContext())
        {
            db.Airports.Add(new Airport { Ident = "FOUL", IcaoCode = "FOUL", Name = "Foul Field", Latitude = lat, Longitude = lon, Kind = AirportKind.MediumAirport });
            var g = TradeCatalog.Goods[0];
            db.InventoryLots.Add(new InventoryLot
            {
                Id = Guid.NewGuid(), CompanyId = companyId, Good = g.Key, Quantity = 10,
                UnitCostCents = 1, LocationIcao = "FOUL", AcquiredAt = clock.UtcNow, UpdatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var good = TradeCatalog.Goods[0];
        long expectedSell = new MarketService(clock, Cfg).Quote("FOUL", good, 0, Cfg.WeatherDemandFactor(visSm)).SellCents;
        using (var db = tdb.NewContext())
        {
            var result = await Trade(db, clock).SellAsync(companyId, "FOUL", good.Key, 10);
            Assert.Equal(expectedSell * 10, result.ProceedsCents); // settlement paid the weather-lifted price shown on the board
        }
    }
}
