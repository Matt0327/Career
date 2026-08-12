using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class FinanceServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    [Fact]
    public async Task NetWorth_Sums_Cash_PlusAssets_MinusLiabilities()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        long expectedAircraft;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var type = new AircraftType
            {
                Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172",
                Category = AircraftCategory.LightSingle, Seats = 4, UsefulLoadLbs = 900, CruiseKtas = 120,
            };
            db.AddRange(company, type);
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM",
                HullConditionMilli = 100_000, EngineConditionMilli = 100_000, // pristine
            });
            db.InventoryLots.Add(new InventoryLot
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, Good = "coffee", Quantity = 10,
                UnitCostCents = 9_000, LocationIcao = "EHAM", AcquiredAt = clock.UtcNow,
            });
            db.Loans.Add(new Loan
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, Tier = 2, PrincipalCents = 5_000_000, AprBps = 1_000,
                TermDays = 90, OutstandingCents = 4_000_000, Status = LoanStatus.Active,
                TakenAt = clock.UtcNow, PaymentLastBilledAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 250_000m, "start");
            companyId = company.Id;
            long market = AircraftPricing.Quote(Cfg, type).TotalCents;
            expectedAircraft = (long)System.Math.Round(market * Cfg.AircraftResaleFactor);
        }

        using (var db = tdb.NewContext())
        {
            var nw = await new FinanceService(db, clock, Cfg).NetWorthAsync(companyId);
            Assert.Equal(25_000_000, nw.CashCents);            // $250k
            Assert.Equal(expectedAircraft, nw.AircraftCents);  // pristine C172 resale value
            Assert.Equal(90_000, nw.InventoryCents);           // 10 × $90 cost basis
            Assert.Equal(4_000_000, nw.LoansCents);            // outstanding principal
            Assert.Equal(25_000_000 + expectedAircraft + 90_000 - 4_000_000, nw.NetWorthCents);
        }
    }

    [Fact]
    public async Task Pnl_AggregatesLedger_ByCategory_OverWindow()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = Guid.NewGuid();
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(new Company { Id = companyId, Name = "Co" });
            await db.SaveChangesAsync();
            var ledger = new LedgerService(db, clock);
            await ledger.PostAsync(companyId, LedgerCategory.JobPayout, 2_000m, "job");
            await ledger.PostAsync(companyId, LedgerCategory.AirportFee, -250m, "fee");
            await ledger.PostAsync(companyId, LedgerCategory.StaffWage, -400m, "wage");
        }

        using (var db = tdb.NewContext())
        {
            var pnl = await new FinanceService(db, clock, Cfg).ProfitLossAsync(companyId, 30);
            Assert.Equal(200_000, pnl.IncomeCents);   // +$2,000
            Assert.Equal(-65_000, pnl.ExpenseCents);  // −$650
            Assert.Equal(135_000, pnl.NetCents);
            Assert.Contains(pnl.Lines, l => l.Category == "JobPayout" && l.NetCents == 200_000);
            Assert.Contains(pnl.Lines, l => l.Category == "StaffWage" && l.NetCents == -40_000);
        }
    }

    [Fact]
    public async Task NetWorth_WornAircraft_IsWorthLess()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle };
            db.AddRange(company, type);
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM",
                HullConditionMilli = 50_000, EngineConditionMilli = 50_000, // half worn
            });
            await db.SaveChangesAsync();
            companyId = company.Id;
        }
        using var db2 = tdb.NewContext();
        var nw = await new FinanceService(db2, clock, Cfg).NetWorthAsync(companyId);
        var type2 = new AircraftType { Category = AircraftCategory.LightSingle };
        long pristine = (long)System.Math.Round(AircraftPricing.Quote(Cfg, type2).TotalCents * Cfg.AircraftResaleFactor);
        Assert.True(nw.AircraftCents < pristine); // condition halves the resale value
    }
}
