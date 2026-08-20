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
    int Aircraft, int Bases, int Routes, int LoansPaidOff, int Policies, long NetWorthCents);

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

        var flights = await _db.Flights.CountAsync(ct);
        var smooth = await _db.Flights.CountAsync(f => f.TouchdownFpm >= -60 && f.TouchdownFpm <= 60, ct);
        var aircraft = await _db.AircraftInstances.CountAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.Ownership == OwnershipKind.Owned, ct); // fleet-size counts OWNED tails, not rentals (Phase 9f)
        var bases = await _db.Bases.CountAsync(b => b.CompanyId == companyId && !b.IsDeleted, ct);
        var routes = await _db.Routes.CountAsync(r => r.CompanyId == companyId, ct); // "ever opened"
        var loansPaid = await _db.Loans.CountAsync(l => l.CompanyId == companyId && l.Status == LoanStatus.PaidOff, ct);
        var policies = await _db.InsurancePolicies.CountAsync(p => p.CompanyId == companyId, ct); // "ever insured"
        var quals = pilot is null ? 0 : await _db.PilotQualifications.CountAsync(q => q.PilotId == pilot.Id, ct);
        var netWorth = (await _finance.NetWorthAsync(companyId, ct)).NetWorthCents;

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
            NetWorthCents: netWorth);
    }
}
