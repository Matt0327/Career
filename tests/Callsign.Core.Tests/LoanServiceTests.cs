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

    // ── Credit rating (Phase 7g): your track record sets your borrowing terms ──

    [Fact]
    public void CreditScore_FreshCompany_IsTheNeutralPivot_NoAprAdjustment()
    {
        int score = Cfg.CreditScore(paidOffLoans: 0, defaultedLoans: 0, outstandingCents: 0, cashCents: 5_000_000);
        Assert.Equal(Cfg.CreditPivotScore, score);
        Assert.Equal(0, Cfg.CreditAprDeltaBps(score));
        Assert.Equal(1_000, Cfg.EffectiveAprBps(1_000, score)); // the tier's listed rate, untouched
    }

    [Fact]
    public void CreditScore_PaidRecordDiscounts_DefaultAndLeveragePenalise()
    {
        int good = Cfg.CreditScore(paidOffLoans: 4, defaultedLoans: 0, outstandingCents: 0, cashCents: 10_000_000);
        int defaulted = Cfg.CreditScore(paidOffLoans: 0, defaultedLoans: 1, outstandingCents: 0, cashCents: 10_000_000);
        int levered = Cfg.CreditScore(paidOffLoans: 0, defaultedLoans: 0, outstandingCents: 10_000_000, cashCents: 0);

        Assert.True(good > Cfg.CreditPivotScore);
        Assert.True(defaulted < Cfg.CreditPivotScore);
        Assert.True(levered < Cfg.CreditPivotScore);                 // carrying debt against little cash hurts
        Assert.True(Cfg.CreditAprDeltaBps(good) < 0);                // a discount
        Assert.True(Cfg.CreditAprDeltaBps(defaulted) > 0);           // a premium
        Assert.True(Cfg.EffectiveAprBps(1_000, good) < Cfg.EffectiveAprBps(1_000, defaulted));
    }

    [Fact]
    public void EffectiveAprBps_NeverDropsBelowTheFloor()
    {
        int best = Cfg.CreditScore(paidOffLoans: Cfg.CreditPaidLoanCap, defaultedLoans: 0, outstandingCents: 0, cashCents: 1_000_000_000);
        Assert.True(Cfg.EffectiveAprBps(200, best) >= Cfg.CreditAprFloorBps); // even the deepest discount is floored
    }

    // A prior loan the company carried for ~60 days before clearing (seasoned) unless heldDays says otherwise.
    private static Loan HistoryLoan(Guid companyId, DateTimeOffset at, LoanStatus status, int heldDays = 60)
        => new() { Id = Guid.NewGuid(), CompanyId = companyId, Tier = 2, PrincipalCents = 20_000_000, AprBps = 1_000,
                   TermDays = 90, OutstandingCents = 0, Status = status, TakenAt = at, PaymentLastBilledAt = at.AddDays(heldDays) };

    [Fact]
    public async Task Take_AfterADefault_ChargesAPremiumOverTheTierRate()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock, 100_000_000); // $1M cash
        using (var db = tdb.NewContext())
        {
            db.Loans.Add(HistoryLoan(companyId, clock.UtcNow, LoanStatus.Defaulted));
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var loan = await Loans(db, clock).TakeAsync(companyId, 20_000_000); // Business tier, listed 10%
            Assert.True(loan.AprBps > 1_000); // a default premium above the listed rate
        }
    }

    [Fact]
    public async Task Take_AfterCleanRepayments_ChargesADiscount()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock, 100_000_000);
        using (var db = tdb.NewContext())
        {
            for (int i = 0; i < 3; i++)
                db.Loans.Add(HistoryLoan(companyId, clock.UtcNow, LoanStatus.PaidOff));
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var loan = await Loans(db, clock).TakeAsync(companyId, 20_000_000);
            Assert.True(loan.AprBps < 1_000); // a good-credit discount below the listed rate
        }
        using (var db = tdb.NewContext())
        {
            // The offer surface reflects the rating: a strong score discounts every tier.
            var (credit, offers) = await Loans(db, clock).OffersForAsync(companyId);
            Assert.True(credit.Score > Cfg.CreditPivotScore);
            Assert.True(credit.AprDeltaBps < 0);
            Assert.All(offers, o => Assert.True(o.EffectiveAprBps <= o.Tier.AprBps));
        }
    }

    [Fact]
    public async Task SameDayTakeAndRepay_DoesNotBuildCredit()
    {
        // Anti-farm: loans flipped the same day (zero interest, held < seasoning) prove nothing, so five of
        // them leave the rating at the neutral pivot — you cannot conjure a free discount.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var companyId = await SeedCompanyAsync(tdb, clock, 100_000_000);
        using (var db = tdb.NewContext())
        {
            for (int i = 0; i < 5; i++)
                db.Loans.Add(HistoryLoan(companyId, clock.UtcNow, LoanStatus.PaidOff, heldDays: 0)); // same-day churn
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var credit = await Loans(db, clock).AssessCreditAsync(companyId);
            Assert.Equal(Cfg.CreditPivotScore, credit.Score); // unseasoned repayments don't count
            Assert.Equal(0, credit.AprDeltaBps);
        }
    }
}
