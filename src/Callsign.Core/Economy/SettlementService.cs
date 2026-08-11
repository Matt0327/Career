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

    public async Task<SettlementResult> SettleAsync(Guid assignmentId, FlightRecord flight,
        Guid? aircraftInstanceId = null, CancellationToken ct = default)
    {
        var a = await _db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct)
                ?? throw new InvalidOperationException($"Assignment {assignmentId} not found.");
        if (a.Status == AssignmentStatus.Settled)
            throw new InvalidOperationException("Assignment already settled.");

        // Resolve everything that can fail BEFORE any money is staged.
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == a.PilotId, ct)
                    ?? throw new InvalidOperationException($"Pilot {a.PilotId} not found for assignment {a.Id}.");

        var now = _clock.UtcNow;
        long baseCents = a.RewardQuoteCents; // frozen quote, never the live job

        // Landing delta in DECIMAL with away-from-zero rounding — the one money convention (ToCents).
        long landingDelta = (long)Math.Round(
            (decimal)baseCents * _cfg.LandingModifierPct(flight.TouchdownFpm), MidpointRounding.AwayFromZero);

        var type = await MatchAircraftAsync(flight.AircraftTitle, ct);
        // "Right aircraft" bonus: passenger charters need SEATS for everyone; cargo needs useful LOAD.
        bool payloadMatched = a.Type.CarriesPassengers()
            ? type?.Seats is int seats && seats >= a.Pax
            : type?.UsefulLoadLbs is int usefulLoad && usefulLoad >= a.WeightLbs;
        int xp = a.XpQuote + (payloadMatched ? (int)Math.Round(a.XpQuote * _cfg.PayloadMatchXpBonusPct) : 0);

        var jobRef = a.JobId.ToString();
        var postings = new List<LedgerPosting>
        {
            new(LedgerCategory.JobPayout, baseCents / 100m, $"{a.Type} payout to {a.DestIcao}",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:base"),
        };
        var lines = new List<PayoutLine> { new("Base reward", baseCents) };

        if (landingDelta > 0)
        {
            postings.Add(new(LedgerCategory.JobBonus, landingDelta / 100m, "Smooth landing bonus",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:landing"));
            lines.Add(new($"Landing bonus ({flight.TouchdownFpm:F0} fpm)", landingDelta));
        }
        else if (landingDelta < 0)
        {
            postings.Add(new(LedgerCategory.Penalty, landingDelta / 100m, "Hard-landing penalty",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:landing"));
            lines.Add(new($"Landing penalty ({flight.TouchdownFpm:F0} fpm)", landingDelta));
        }

        // Landing/handling fee at the destination — a running cost, itemised like everything else.
        // Waived if you own a base there.
        var destAirport = await _db.Airports.FirstOrDefaultAsync(x => x.Ident == a.DestIcao, ct);
        bool ownBaseAtDest = await _db.Bases.AnyAsync(b => b.CompanyId == a.AccountId && b.AirportIcao == a.DestIcao && b.IsActive, ct);
        long landingFee = destAirport is not null && !ownBaseAtDest ? _cfg.LandingFeeCents(destAirport.Kind) : 0;
        if (landingFee > 0)
        {
            postings.Add(new(LedgerCategory.AirportFee, -(landingFee / 100m), $"Landing fee at {a.DestIcao}",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:fee"));
            lines.Add(new($"Landing fee ({a.DestIcao})", -landingFee));
        }

        long total = baseCents + landingDelta - landingFee;
        var breakdown = new PayoutBreakdown(total, lines);

        // Stage the ledger rows + cash delta (not saved), then commit them together with the Flight
        // row, XP, and status in a single transaction. The per-line DedupeKeys make a retry idempotent.
        await _ledger.StageBatchAsync(a.AccountId, postings, ct);

        var flightEntity = new FlightEntity
        {
            Id = Guid.NewGuid(),
            JobAssignmentId = a.Id,
            FlownByPilotId = a.PilotId,
            AircraftTitle = flight.AircraftTitle,
            AircraftTypeId = type?.Id,
            AircraftInstanceId = aircraftInstanceId,
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
        pilot.Xp += xp;
        pilot.CurrentIcao = a.DestIcao; // the pilot ends the leg at the destination — the loop moves along
        a.Status = AssignmentStatus.Settled;
        a.SettledAt = now;

        // If the leg was flown in an owned airframe, it moves to the destination and ticks airframe hours.
        if (aircraftInstanceId is { } aid)
        {
            var instance = await _db.AircraftInstances.FirstOrDefaultAsync(x => x.Id == aid, ct);
            if (instance is not null)
            {
                var hours = Math.Max(0, flight.BlockTime.TotalHours);
                instance.LocationIcao = a.DestIcao;
                instance.Availability = AircraftAvailability.Available;
                instance.AirframeHours += hours;

                // Wear: hull + engine drop with hours; a hard touchdown adds extra hull wear.
                int hourWear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour);
                int hullWear = hourWear + (_cfg.LandingModifierPct(flight.TouchdownFpm) < 0 ? _cfg.HardLandingWearMilli : 0);
                instance.HullConditionMilli = Math.Max(0, instance.HullConditionMilli - hullWear);
                instance.EngineConditionMilli = Math.Max(0, instance.EngineConditionMilli - hourWear);
                instance.UpdatedAt = now;
            }
        }

        // Trade goods ride with the pilot: any lots at the departure airport travel to the destination
        // (Phase 2g). Staged here so they move in the same transaction as the rest of the settlement.
        var moving = await _db.InventoryLots
            .Where(l => l.CompanyId == a.AccountId && l.LocationIcao == a.OriginIcao && l.Quantity > 0 && !l.IsDeleted)
            .ToListAsync(ct);
        foreach (var lot in moving)
        {
            lot.LocationIcao = a.DestIcao;
            lot.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct); // one transaction: ledger rows + cash + flight + xp + status + airframe + goods

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

        // Fallback: substring either way, but only for aliases long enough not to false-match a short
        // token onto the wrong type (§5.3 — never fail a flight over identity, but don't mis-bind either).
        var aliases = await _db.AircraftTitleAliases.ToListAsync(ct);
        var m = aliases.FirstOrDefault(x => x.TitleNormalized.Length >= 5
            && (norm.Contains(x.TitleNormalized) || x.TitleNormalized.Contains(norm)));
        return m is null ? null : await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == m.AircraftTypeId, ct);
    }
}
