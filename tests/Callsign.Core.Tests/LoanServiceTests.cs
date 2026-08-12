using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class LoanServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static LoanService Loans(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);
    private static OperationsService Ops(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    private static async Task<Guid> SeedCompanyAsync(TestDb tdb, IClock clock, long cashCents = 0)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        if (cashCents != 0)
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return company.Id;
    }

    [Theory]
    [InlineData(1_000_000, 1)]      // $10k  -> Starter
    [InlineData(20_000_000, 2)]     // $200k -> Business
    [InlineData(200_000_000, 3)]    // $2M   -> Commercial
    [InlineData(1_000_000_000, 4)]  // $10M  -> Fleet
    public void TierFor_SelectsBand(long principal, int tier)
        => Assert.Equal(tier, LoanCatalog.TierFor(principal)!.Tier);

    [Fact]
    public void Amortize_DecliningInterest_AndStraightLinePrincipal()
    {
        var (interest, principal) = LoanCatalog.Amortize(10_000_000, 10_000_000, 1_000, 90, 30); // $100k @10%, 30 of 90 days
        Assert.True(interest > 0);
        Assert.InRange(principal, 3_000_000, 3_600_000); // ~a third of the principal
    }

    [Fact]
    public async Task Take_CreditsCash_AndCarriesTheDebt()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock);
        using (var db = tdb.NewContext())
        {
            var loan = await Loans(db, clock).TakeAsync(companyId, 20_000_000); // $200k -> Business @10%
            Assert.Equal(2, loan.Tier);
            Assert.Equal(1_000, loan.AprBps);
            Assert.Equal(20_000_000, loan.OutstandingCents);
        }
        using (var db = tdb.NewContext())
        {
            Assert.Equal(20_000_000, (await db.Companies.FindAsync(companyId))!.CashCents); // drawn down to cash
            var credit = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.LoanPrincipal);
            Assert.Equal(20_000_000, credit.AmountCents);
        }
    }

    [Fact]
    public async Task Take_OutsideRange_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Loans(db, clock).TakeAsync(companyId, 1_000)); // $10 too small
    }

    [Fact]
    public async Task Reconcile_BillsInterestAndPrincipal_ReducingOutstanding()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Loans(db, clock).TakeAsync(companyId, 9_000_000);

        clock.UtcNow = clock.UtcNow.AddDays(30);
        using (var db = tdb.NewContext())
            Assert.True((await Ops(db, clock).ReconcileAsync(companyId)).LoanCents > 0);
        using (var db = tdb.NewContext())
        {
            var loan = await db.Loans.SingleAsync(l => l.CompanyId == companyId);
            Assert.True(loan.OutstandingCents < 9_000_000);
            var cats = (await db.LedgerEntries.ToListAsync()).Select(e => e.Category).ToList();
            Assert.Contains(LedgerCategory.LoanInterest, cats);
            Assert.Contains(LedgerCategory.LoanPayment, cats);
        }
    }

    [Fact]
    public async Task Reconcile_OverFullTerm_PaysOff()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Loans(db, clock).TakeAsync(companyId, 5_000_000);

        clock.UtcNow = clock.UtcNow.AddDays(Cfg.LoanTermDays + 5);
        using (var db = tdb.NewContext())
            await Ops(db, clock).ReconcileAsync(companyId);
        using (var db = tdb.NewContext())
        {
            var loan = await db.Loans.SingleAsync(l => l.CompanyId == companyId);
            Assert.Equal(0, loan.OutstandingCents);
            Assert.Equal(LoanStatus.PaidOff, loan.Status);
        }
    }

    [Fact]
    public async Task Payoff_ClearsLoan_InFull()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock, 100_000_000);
        Guid loanId;
        using (var db = tdb.NewContext())
            loanId = (await Loans(db, clock).TakeAsync(companyId, 20_000_000)).Id;

        clock.UtcNow = clock.UtcNow.AddDays(10);
        using (var db = tdb.NewContext())
            Assert.True(await Loans(db, clock).PayoffAsync(companyId, loanId) >= 20_000_000); // principal + interest
        using (var db = tdb.NewContext())
        {
            var loan = await db.Loans.SingleAsync(l => l.Id == loanId);
            Assert.Equal(0, loan.OutstandingCents);
            Assert.Equal(LoanStatus.PaidOff, loan.Status);
        }
    }
}
