using System.Text.Json;
using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using FlightEntity = Callsign.Core.Domain.Flight;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Economy;

/// <summary>
/// Settles a completed flight against its frozen assignment: itemises the payout (base reward +
/// landing-quality bonus/penalty), writes every line to the ledger AND the Flight row + XP + status
/// in ONE transaction, and marks the assignment settled. It reads the FROZEN quote on the
/// assignment, never the live job.
/// </summary>
public sealed class SettlementService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public SettlementService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    public async Task<SettlementResult> SettleAsync(Guid assignmentId, FlightRecord flight, CancellationToken ct = default)
    {
        var a = await _db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct)
                ?? throw new InvalidOperationException($"Assignment {assignmentId} not found.");
        if (a.Status == AssignmentStatus.Settled)
            throw new InvalidOperationException("Assignment already settled.");

        var now = _clock.UtcNow;
        long baseCents = a.RewardQuoteCents; // frozen quote, never the live job
        long landingDelta = (long)Math.Round(baseCents * _cfg.LandingModifierPct(flight.TouchdownFpm));

        var type = await MatchAircraftAsync(flight.AircraftTitle, ct);
        bool payloadMatched = type?.UsefulLoadLbs is int usefulLoad && usefulLoad >= a.WeightLbs;
        int xp = a.XpQuote + (payloadMatched ? (int)Math.Round(a.XpQuote * _cfg.PayloadMatchXpBonusPct) : 0);

        var jobRef = a.JobId.ToString();
        var postings = new List<LedgerPosting>
        {
            new(LedgerCategory.JobPayout, baseCents / 100m, $"{a.Type} payout to {a.DestIcao}", LedgerRefType.Job, jobRef),
        };
        var lines = new List<PayoutLine> { new("Base reward", baseCents) };

        if (landingDelta > 0)
        {
            postings.Add(new(LedgerCategory.JobBonus, landingDelta / 100m, "Smooth landing bonus", LedgerRefType.Job, jobRef));
            lines.Add(new($"Landing bonus ({flight.TouchdownFpm:F0} fpm)", landingDelta));
        }
        else if (landingDelta < 0)
        {
            postings.Add(new(LedgerCategory.Penalty, landingDelta / 100m, "Hard-landing penalty", LedgerRefType.Job, jobRef));
            lines.Add(new($"Landing penalty ({flight.TouchdownFpm:F0} fpm)", landingDelta));
        }

        long total = baseCents + landingDelta;
        var breakdown = new PayoutBreakdown(total, lines);

        // Stage the ledger rows + cash delta (not saved), then commit them together with the Flight
        // row, XP, and status in a single transaction.
        await _ledger.StageBatchAsync(a.AccountId, postings, ct);

        var flightEntity = new FlightEntity
        {
            Id = Guid.NewGuid(),
            JobAssignmentId = a.Id,
            FlownByPilotId = a.PilotId,
            AircraftTitle = flight.AircraftTitle,
            AircraftTypeId = type?.Id,
            DepartedAt = flight.DepartedAt,
            ArrivedAt = flight.ArrivedAt,
            TouchdownFpm = flight.TouchdownFpm,
            DistanceNm = flight.DistanceNm,
            FuelUsedLbs = flight.FuelUsedLbs,
            PayoutCents = total,
            Xp = xp,
            PayoutBreakdownJson = JsonSerializer.Serialize(breakdown),
            SettledAt = now,
        };
        _db.Flights.Add(flightEntity);

        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == a.PilotId, ct);
        if (pilot is not null)
            pilot.Xp += xp;

        a.Status = AssignmentStatus.Settled;
        a.SettledAt = now;

        await _db.SaveChangesAsync(ct); // one transaction: ledger rows + cash + flight + xp + status

        return new SettlementResult(flightEntity.Id, total, xp, payloadMatched, breakdown);
    }

    private async Task<AircraftType?> MatchAircraftAsync(string title, CancellationToken ct)
    {
        var norm = AircraftTitle.Normalize(title);
        if (norm.Length == 0)
            return null;

        var exact = await _db.AircraftTitleAliases.FirstOrDefaultAsync(x => x.TitleNormalized == norm, ct);
        if (exact is not null)
            return await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == exact.AircraftTypeId, ct);

        // Fallback: substring match either way (small roster, brief §5.3 — never fail over identity).
        var aliases = await _db.AircraftTitleAliases.ToListAsync(ct);
        var m = aliases.FirstOrDefault(x => norm.Contains(x.TitleNormalized) || x.TitleNormalized.Contains(norm));
        return m is null ? null : await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == m.AircraftTypeId, ct);
    }
}
