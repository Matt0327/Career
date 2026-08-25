using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Progression;

/// <summary>
/// A cheap snapshot of a company's progress, gathered from data the game already keeps (no new
/// bookkeeping). It's the shared read model behind both achievements (Phase 5a) and campaigns (Phase 5b),
/// so recognition and story judge milestones off exactly the same numbers.
/// </summary>
public sealed record ProgressMetrics(
    int Flights, int SmoothLandings, int RankIndex, int Qualifications, int ReputationMilli,
    int Aircraft, int Bases, int Routes, int LoansPaidOff, int Policies, long NetWorthCents,
    // Phase 11a — the airline's own operating reputation (0–100000), distinct from the pilot's ReputationMilli.
    int OperatingReputationMilli = 0,
    // Phase 13 — lifetime flying totals + relationships, so the achievement roster can be varied without new plumbing.
    long TotalDistanceNm = 0, long LifetimeEarningsCents = 0, long LongestLegNm = 0, int BestScore = 0, int Clients = 0);

public sealed class ProgressMetricsService
{
    private readonly CallsignDbContext _db;
    private readonly FinanceService _finance;

    public ProgressMetricsService(CallsignDbContext db, FinanceService finance)
    {
        _db = db;
        _finance = finance;
    }

    // Counts are company-scoped; the flight log is the whole company's (one save = one company), matching
    // the /api/game/state flight count. Landings within ±60 fpm read as "butter" in the UI.
    public async Task<ProgressMetrics> SnapshotAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId, ct);
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);

        var flights = await _db.Flights.CountAsync(ct);
        var smooth = await _db.Flights.CountAsync(f => f.TouchdownFpm >= -60 && f.TouchdownFpm <= 60, ct);
        var aircraft = await _db.AircraftInstances.CountAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.Ownership == OwnershipKind.Owned, ct); // fleet-size counts OWNED tails, not rentals (Phase 9f)
        var bases = await _db.Bases.CountAsync(b => b.CompanyId == companyId && !b.IsDeleted, ct);
        var routes = await _db.Routes.CountAsync(r => r.CompanyId == companyId, ct); // "ever opened"
        var loansPaid = await _db.Loans.CountAsync(l => l.CompanyId == companyId && l.Status == LoanStatus.PaidOff, ct);
        var policies = await _db.InsurancePolicies.CountAsync(p => p.CompanyId == companyId, ct); // "ever insured"
        var quals = pilot is null ? 0 : await _db.PilotQualifications.CountAsync(q => q.PilotId == pilot.Id, ct);
        var netWorth = (await _finance.NetWorthAsync(companyId, ct)).NetWorthCents;
        // Phase 13 — lifetime flying totals + client count, for the wider achievement roster.
        var totalDist = flights == 0 ? 0 : (long)Math.Round(await _db.Flights.SumAsync(f => f.DistanceNm, ct));
        var lifetimeEarn = flights == 0 ? 0 : await _db.Flights.SumAsync(f => f.PayoutCents, ct);
        var longestLeg = flights == 0 ? 0 : (long)Math.Round(await _db.Flights.MaxAsync(f => f.DistanceNm, ct));
        var bestScore = await _db.Flights.Where(f => f.OverallScore != null).Select(f => f.OverallScore!.Value).OrderByDescending(s => s).FirstOrDefaultAsync(ct);
        var clients = await _db.Clients.CountAsync(c => c.CompanyId == companyId, ct);

        return new ProgressMetrics(
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
            NetWorthCents: netWorth,
            OperatingReputationMilli: company?.OperatingReputationMilli ?? 0,
            TotalDistanceNm: totalDist,
            LifetimeEarningsCents: lifetimeEarn,
            LongestLegNm: longestLeg,
            BestScore: bestScore,
            Clients: clients);
    }
}
