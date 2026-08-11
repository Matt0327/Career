using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Economy;

/// <summary>What a check-flight attempt produced.</summary>
public sealed record CheckFlightResult(bool Passed, QualClass Class, int Stars, long FeeCents, double TouchdownFpm);

/// <summary>
/// Check-flights (Phase 3d): pay a fee to fly a scored landing and earn — or upgrade — a licence class.
/// The fee is charged for the attempt whether you pass or fail (a booked examiner isn't free). A pass
/// awards/refines the <see cref="PilotQualification"/> (stars from the touchdown, best-fpm kept); the
/// fee and the qualification move together in one transaction. Reuses the same landing score as jobs.
/// </summary>
public sealed class CheckFlightService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public CheckFlightService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    public long FeeCents(QualClass cls) => _cfg.CheckFlightFeeCents(cls);

    /// <summary>Grade a flown check-ride for <paramref name="cls"/>: bill the fee, and on a pass award/upgrade the class.</summary>
    public async Task<CheckFlightResult> AttemptAsync(
        Guid companyId, Guid pilotId, QualClass cls, FlightRecord flight, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        _ = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId, ct)
            ?? throw new InvalidOperationException($"Pilot {pilotId} not found.");

        long fee = _cfg.CheckFlightFeeCents(cls);
        if (company.CashCents < fee)
            throw new InvalidOperationException(
                $"Not enough cash: a {QualificationClasses.Def(cls).DisplayName} check-flight costs {fee / 100m:C0}, you have {company.Cash:C0}.");

        int stars = _cfg.CheckFlightStars(flight.TouchdownFpm);
        bool passed = stars > 0;
        var now = _clock.UtcNow;

        if (passed)
        {
            double td = Math.Abs(flight.TouchdownFpm);
            var q = await _db.PilotQualifications.FirstOrDefaultAsync(
                x => x.PilotId == pilotId && x.Class == cls && !x.IsDeleted, ct);
            if (q is null)
            {
                _db.PilotQualifications.Add(new PilotQualification
                {
                    Id = Guid.NewGuid(), PilotId = pilotId, Class = cls, Stars = stars,
                    BestTouchdownFpm = td, EarnedAt = now, UpdatedAt = now,
                });
            }
            else
            {
                q.Stars = Math.Max(q.Stars, stars);
                q.BestTouchdownFpm = q.BestTouchdownFpm is double best ? Math.Min(best, td) : td;
                q.UpdatedAt = now;
            }
        }

        string outcome = passed ? $"passed ({stars}★)" : "failed";
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.CheckFlightFee, -(fee / 100m),
                $"{QualificationClasses.Def(cls).DisplayName} check-flight — {outcome}",
                LedgerRefType.CheckFlight, pilotId.ToString()),
        }, ct);
        await _db.SaveChangesAsync(ct);

        return new CheckFlightResult(passed, cls, passed ? stars : 0, fee, flight.TouchdownFpm);
    }
}
