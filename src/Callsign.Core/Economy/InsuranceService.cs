using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A priced insurance quote for an airframe (nothing committed).</summary>
public sealed record InsuranceQuote(
    long HullValueCents, int CoverageMilli, long CoveredValueCents,
    long PremiumPerWeekCents, long DeductibleCents, long ClaimPayoutCents);

/// <summary>
/// Insurance (Phase 4c): a policy + claim path. Insuring an airframe sets a weekly premium (billed in the
/// reconcile pass) for coverage of a fraction of its hull value; a total loss (the airframe worn to the
/// write-off threshold) can be claimed for the covered value minus the deductible, retiring the airframe.
/// The premium is a running cost; the claim turns a catastrophe into a deductible instead of a total loss.
/// </summary>
public sealed class InsuranceService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public InsuranceService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    public Task<List<InsurancePolicy>> GetActiveAsync(Guid companyId, CancellationToken ct = default)
        => _db.InsurancePolicies.Where(p => p.CompanyId == companyId && p.Active && !p.IsDeleted).ToListAsync(ct);

    public async Task<InsuranceQuote?> QuoteAsync(Guid companyId, Guid aircraftInstanceId, int? coverageMilli, CancellationToken ct = default)
    {
        var inst = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftInstanceId && a.CompanyId == companyId && !a.IsDeleted, ct);
        if (inst is null) return null;
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId, ct);
        if (type is null) return null;
        long hull = AircraftPricing.Quote(_cfg, type).TotalCents;
        int cov = Math.Clamp(coverageMilli ?? _cfg.InsuranceDefaultCoverageMilli, 10_000, 100_000);
        long covered = (long)(hull * (cov / 100_000.0));
        long deductible = _cfg.InsuranceDeductibleCents(covered);
        return new InsuranceQuote(hull, cov, covered, _cfg.InsurancePremiumPerWeekCents(covered), deductible, Math.Max(0, covered - deductible));
    }

    public async Task<InsurancePolicy> InsureAsync(Guid companyId, Guid aircraftInstanceId, int? coverageMilli, CancellationToken ct = default)
    {
        var inst = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftInstanceId && a.CompanyId == companyId && !a.IsDeleted, ct)
                   ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (await _db.InsurancePolicies.AnyAsync(p => p.AircraftInstanceId == aircraftInstanceId && p.Active && !p.IsDeleted, ct))
            throw new InvalidOperationException("That aircraft is already insured.");
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId, ct)
                   ?? throw new InvalidOperationException("Aircraft type not found.");

        long hull = AircraftPricing.Quote(_cfg, type).TotalCents;
        int cov = Math.Clamp(coverageMilli ?? _cfg.InsuranceDefaultCoverageMilli, 10_000, 100_000);
        long covered = (long)(hull * (cov / 100_000.0));
        var now = _clock.UtcNow;

        var policy = new InsurancePolicy
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Scope = InsuranceScope.Aircraft, AircraftInstanceId = aircraftInstanceId,
            HullValueCents = hull, CoverageMilli = cov, DeductibleCents = _cfg.InsuranceDeductibleCents(covered),
            PremiumPerWeekCents = _cfg.InsurancePremiumPerWeekCents(covered), PremiumLastBilledAt = now,
            Active = true, StartedAt = now, UpdatedAt = now,
        };
        _db.InsurancePolicies.Add(policy);
        await _db.SaveChangesAsync(ct);
        return policy;
    }

    public async Task CancelAsync(Guid companyId, Guid policyId, CancellationToken ct = default)
    {
        var policy = await _db.InsurancePolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.CompanyId == companyId && p.Active, ct);
        if (policy is null)
            return;
        var now = _clock.UtcNow;
        int days = (int)Math.Floor((now - policy.PremiumLastBilledAt).TotalDays);
        long premium = PremiumForDays(policy.PremiumPerWeekCents, days);
        if (premium > 0)
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.InsurancePremium, -(premium / 100m), "Insurance premium (final)",
                    LedgerRefType.InsuranceClaim, policy.Id.ToString(), DedupeKey: $"ins-prem:{policy.Id}:{policy.PremiumLastBilledAt.UtcTicks}"),
            }, ct);
        policy.PremiumLastBilledAt = now;
        policy.Active = false;
        policy.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>File a claim on a written-off airframe: pay out covered − deductible, and retire the airframe.</summary>
    public async Task<long> ClaimAsync(Guid companyId, Guid policyId, CancellationToken ct = default)
    {
        var policy = await _db.InsurancePolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.CompanyId == companyId && p.Active, ct)
                     ?? throw new InvalidOperationException("Policy not found.");
        var inst = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == policy.AircraftInstanceId && !a.IsDeleted, ct)
                   ?? throw new InvalidOperationException("Insured aircraft not found.");
        int cond = Math.Min(inst.HullConditionMilli, inst.EngineConditionMilli);
        if (cond > _cfg.InsuranceTotalLossConditionMilli)
            throw new InvalidOperationException(
                $"{inst.Tail} isn't a write-off — a claim needs it at or below {_cfg.InsuranceTotalLossConditionMilli / 1000}% condition (it's at {cond / 1000}%).");

        long payout = policy.ClaimPayoutCents;
        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.InsuranceClaim, payout / 100m, $"Insurance claim — {inst.Tail} written off",
                LedgerRefType.InsuranceClaim, policy.Id.ToString(), AircraftInstanceId: inst.Id),
        }, ct);
        inst.IsDeleted = true; // written off, removed from the fleet
        inst.UpdatedAt = now;
        policy.Active = false;
        policy.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return payout;
    }

    /// <summary>Premium for a whole number of days, prorated from the weekly rate.</summary>
    public static long PremiumForDays(long perWeekCents, int days)
        => days <= 0 ? 0 : (long)Math.Round(perWeekCents * (days / 7.0));
}
