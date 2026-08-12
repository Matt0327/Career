using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Achievements;

/// <summary>
/// Evaluates the <see cref="AchievementCatalog"/> against a company's current progress and awards any
/// newly-earned badges — idempotently (a unique (CompanyId, Key) index means a badge is granted once).
/// Evaluation is lazy: the read endpoint calls it, so badges catch up whenever the player looks, without
/// hooking every event. Returns the full roster (earned + locked, each with progress) for display.
/// </summary>
public sealed class AchievementService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly FinanceService _finance;

    public AchievementService(CallsignDbContext db, IClock clock, FinanceService finance)
    {
        _db = db;
        _clock = clock;
        _finance = finance;
    }

    public async Task<IReadOnlyList<AchievementView>> EvaluateAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var m = await GatherAsync(companyId, pilotId, ct);

        var earnedAt = await _db.AchievementAwards
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .ToDictionaryAsync(a => a.Key, a => a.EarnedAt, ct);

        var now = _clock.UtcNow;
        var awardedThisPass = false;
        foreach (var def in AchievementCatalog.All)
        {
            if (earnedAt.ContainsKey(def.Key) || !def.IsEarnedBy(m))
                continue;
            _db.AchievementAwards.Add(new AchievementAward
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Key = def.Key,
                EarnedAt = now,
                UpdatedAt = now,
            });
            earnedAt[def.Key] = now;
            awardedThisPass = true;
        }
        if (awardedThisPass)
            await _db.SaveChangesAsync(ct);

        return AchievementCatalog.All.Select(def => new AchievementView(
            def.Key, def.Name, def.Description, def.Category, def.Target,
            Math.Min(def.ProgressOf(m), def.Target),
            earnedAt.ContainsKey(def.Key),
            earnedAt.TryGetValue(def.Key, out var at) ? at : null)).ToList();
    }

    // A single cheap snapshot of everything the catalog reads. Counts are company-scoped; the flight log
    // is the whole company's (one save = one company), matching the /api/game/state flight count.
    private async Task<AchievementMetrics> GatherAsync(Guid companyId, Guid pilotId, CancellationToken ct)
    {
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId, ct);

        var flights = await _db.Flights.CountAsync(ct);
        // Landing rate within ±60 fpm reads as "butter" in the UI; touchdowns are negative on descent.
        var smooth = await _db.Flights.CountAsync(f => f.TouchdownFpm >= -60 && f.TouchdownFpm <= 60, ct);
        var aircraft = await _db.AircraftInstances.CountAsync(a => a.CompanyId == companyId && !a.IsDeleted, ct);
        var bases = await _db.Bases.CountAsync(b => b.CompanyId == companyId && !b.IsDeleted, ct);
        var routes = await _db.Routes.CountAsync(r => r.CompanyId == companyId, ct); // "ever opened"
        var loansPaid = await _db.Loans.CountAsync(l => l.CompanyId == companyId && l.Status == LoanStatus.PaidOff, ct);
        var policies = await _db.InsurancePolicies.CountAsync(p => p.CompanyId == companyId, ct); // "ever insured"
        var quals = pilot is null ? 0 : await _db.PilotQualifications.CountAsync(q => q.PilotId == pilot.Id, ct);
        var netWorth = (await _finance.NetWorthAsync(companyId, ct)).NetWorthCents;

        return new AchievementMetrics(
            Flights: flights,
            SmoothLandings: smooth,
            RankIndex: (int)(pilot?.Rank ?? PilotRank.Trainee),
            Qualifications: quals,
            ReputationMilli: pilot?.ReputationMilli ?? 0,
            Aircraft: aircraft,
            Bases: bases,
            Routes: routes,
            LoansPaidOff: loansPaid,
            Policies: policies,
            NetWorthCents: netWorth);
    }
}
