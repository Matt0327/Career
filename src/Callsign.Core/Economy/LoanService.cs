using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>Your borrowing standing (Phase 7g): a 0–100 score, its letter grade, and the APR adjustment it
/// earns you (negative = a discount off the tier rate, positive = a premium).</summary>
public sealed record CreditAssessment(int Score, string Grade, int AprDeltaBps);

/// <summary>A lending tier priced for YOUR rating: the tier's listed APR plus the credit adjustment.</summary>
public sealed record LoanOffer(LoanTierDef Tier, int EffectiveAprBps);

/// <summary>
/// Loans (Phase 4a): borrow against a tier's APR, draw the cash down through the ledger, and carry the
/// outstanding principal as a liability. Interest + scheduled repayments are billed in the offline
/// reconcile pass (<see cref="OperationsService"/>); this service handles taking a loan and paying one off.
/// Your credit rating (Phase 7g) — built from repayment history and current leverage — shifts the APR.
/// </summary>
public sealed class LoanService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public LoanService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    public IReadOnlyList<LoanTierDef> Offers() => LoanCatalog.Tiers;

    public Task<List<Loan>> GetActiveAsync(Guid companyId, CancellationToken ct = default)
        => _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted)
                    .OrderBy(l => l.TakenAt).ToListAsync(ct);

    /// <summary>
    /// The company's current credit standing, from its loan repayment history and how leveraged it is right
    /// now. A fresh company scores the neutral pivot (its loans price at the listed rate); a clean repayment
    /// record earns a discount, a default or heavy leverage a premium.
    /// </summary>
    public async Task<CreditAssessment> AssessCreditAsync(Guid companyId, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        // A repaid loan only counts if it was actually carried (seasoned) — a same-day take-and-repay accrues
        // no interest and proves nothing, so it can't be farmed for a free rating.
        var paidSpans = await _db.Loans
            .Where(l => l.CompanyId == companyId && l.Status == LoanStatus.PaidOff && !l.IsDeleted)
            .Select(l => new { l.TakenAt, l.PaymentLastBilledAt })
            .ToListAsync(ct);
        int paidOff = paidSpans.Count(l => (l.PaymentLastBilledAt - l.TakenAt).TotalDays >= _cfg.CreditSeasoningDays);
        int defaulted = await _db.Loans.CountAsync(l => l.CompanyId == companyId && l.Status == LoanStatus.Defaulted && !l.IsDeleted, ct);
        long outstanding = await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted)
                                          .SumAsync(l => l.OutstandingCents, ct);
        int score = _cfg.CreditScore(paidOff, defaulted, outstanding, company.CashCents);
        return new CreditAssessment(score, LoanCatalog.CreditGrade(score), _cfg.CreditAprDeltaBps(score));
    }

    /// <summary>The lending tiers priced for the company's current rating, alongside that rating.</summary>
    public async Task<(CreditAssessment Credit, IReadOnlyList<LoanOffer> Offers)> OffersForAsync(Guid companyId, CancellationToken ct = default)
    {
        var credit = await AssessCreditAsync(companyId, ct);
        var offers = LoanCatalog.Tiers.Select(t => new LoanOffer(t, _cfg.EffectiveAprBps(t.AprBps, credit.Score))).ToList();
        return (credit, offers);
    }

    /// <summary>Borrow <paramref name="principalCents"/>: the draw-down credits cash; the debt is carried here.</summary>
    public async Task<Loan> TakeAsync(Guid companyId, long principalCents, CancellationToken ct = default)
    {
        _ = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var tier = LoanCatalog.TierFor(principalCents)
                   ?? throw new InvalidOperationException(
                       $"{principalCents / 100m:C0} is outside the lending range ({LoanCatalog.MinPrincipalCents / 100m:C0}–{LoanCatalog.MaxPrincipalCents / 100m:C0}).");

        // Price the loan for this company's credit standing: the rate is snapshot at draw-down (Phase 7g).
        var credit = await AssessCreditAsync(companyId, ct);
        int aprBps = _cfg.EffectiveAprBps(tier.AprBps, credit.Score);

        var now = _clock.UtcNow;
        var loan = new Loan
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Tier = tier.Tier, PrincipalCents = principalCents,
            AprBps = aprBps, TermDays = _cfg.LoanTermDays, OutstandingCents = principalCents,
            TakenAt = now, PaymentLastBilledAt = now, Status = LoanStatus.Active, UpdatedAt = now,
        };
        _db.Loans.Add(loan);
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.LoanPrincipal, principalCents / 100m,
                $"Loan draw-down — {tier.Name} ({aprBps / 100.0:0.0}% APR, {credit.Grade} rating)", LedgerRefType.Loan, loan.Id.ToString()),
        }, ct);
        await _db.SaveChangesAsync(ct);
        return loan;
    }

    /// <summary>Pay a loan off in full now: interest accrued since the last billing, plus the whole balance.</summary>
    public async Task<long> PayoffAsync(Guid companyId, Guid loanId, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var loan = await _db.Loans.FirstOrDefaultAsync(l => l.Id == loanId && l.CompanyId == companyId && l.Status == LoanStatus.Active, ct)
                   ?? throw new InvalidOperationException("Loan not found.");

        var now = _clock.UtcNow;
        int days = Math.Max(0, (int)Math.Floor((now - loan.PaymentLastBilledAt).TotalDays));
        double dailyRate = loan.AprBps / 10_000.0 / 365.0;
        long interest = (long)Math.Round(loan.OutstandingCents * dailyRate * days);
        long principal = loan.OutstandingCents;
        long total = interest + principal;

        if (company.CashCents < total)
            throw new InvalidOperationException(
                $"Not enough cash to clear this loan: {total / 100m:C0} owed, you have {company.Cash:C0}.");

        var postings = new List<LedgerPosting>();
        if (interest > 0)
            postings.Add(new(LedgerCategory.LoanInterest, -(interest / 100m), "Loan interest (payoff)",
                LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-int:{loan.Id}:{loan.PaymentLastBilledAt.UtcTicks}"));
        postings.Add(new(LedgerCategory.LoanPayment, -(principal / 100m), "Loan paid off in full",
            LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-payoff:{loan.Id}"));
        await _ledger.StageBatchAsync(companyId, postings, ct);

        loan.OutstandingCents = 0;
        loan.Status = LoanStatus.PaidOff;
        loan.PaymentLastBilledAt = now;
        loan.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return total;
    }
}
