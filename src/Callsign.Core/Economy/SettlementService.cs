using System.Text.Json;
using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Progression;
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
        long grossBase = a.RewardQuoteCents; // frozen quote, never the live job

        // Mission completion (Phase 7d): judge the DELIVERY against this mission type's own rules — a firm
        // arrival damages fragile freight, a rough flight loses a VIP client. This scales (or, on a Failed
        // grade, zeroes) the base reward before anything else. Ordinary jobs grade Full and are unchanged.
        var outcome = MissionProfiles.Evaluate(a.Type, flight, a.DeadlineAt);
        long baseCents = outcome.Grade == MissionGrade.Failed
            ? 0
            : (long)Math.Round((decimal)grossBase * outcome.QualityMilli / 100_000m, MidpointRounding.AwayFromZero);

        // Client relationship (Phase 8d): this job came from a named client whose loyalty we've been building.
        // Load their row (or start one), and read loyalty as it stands BEFORE this delivery — a loyal client
        // pays a repeat premium NOW for the trust already earned. The delivery then moves the bond (further below).
        Client? client = null;
        if (a.ClientKey is { } clientKey)
        {
            client = await _db.Clients.FirstOrDefaultAsync(c => c.CompanyId == a.AccountId && c.ClientKey == clientKey, ct);
            if (client is null)
            {
                client = new Client
                {
                    Id = Guid.NewGuid(), CompanyId = a.AccountId, ClientKey = clientKey,
                    Name = a.ClientName ?? clientKey, HomeIcao = a.OriginIcao,
                    LoyaltyMilli = 0, FirstSeenAt = now, UpdatedAt = now,
                };
                _db.Clients.Add(client);
            }
        }
        // The premium rides on the EARNED base (a failed/partial delivery earns a smaller tip too), computed
        // from pre-delivery loyalty so it rewards the history, not this leg.
        long loyaltyBonus = client is not null && baseCents > 0
            ? (long)Math.Round(baseCents * (decimal)_cfg.ClientLoyaltyBonusPct(client.LoyaltyMilli), MidpointRounding.AwayFromZero)
            : 0;

        // The landing lever (Phase 7c): a tracker-flown leg is graded on its whole-flight score — landing
        // ∧ approach ∧ enroute — and a cheated flight forfeits any bonus. A manual/legacy record (no
        // telemetry assessment) falls back to the raw touchdown rate, so those paths settle unchanged.
        decimal perfPct;
        string landingQualifier;
        if (flight.Scored)
        {
            perfPct = _cfg.PerformancePct(flight.OverallScore);
            if (!flight.ScoreValid) perfPct = Math.Min(perfPct, 0m); // anti-cheat: no bonus, penalties stand
            landingQualifier = flight.ScoreValid ? $"score {flight.OverallScore}" : $"score {flight.OverallScore}, voided";
        }
        else
        {
            perfPct = _cfg.LandingModifierPct(flight.TouchdownFpm);
            landingQualifier = $"{flight.TouchdownFpm:F0} fpm";
        }
        // Performance scales the EARNED base — a damaged (partial) delivery earns a smaller bonus too.
        long landingDelta = (long)Math.Round((decimal)baseCents * perfPct, MidpointRounding.AwayFromZero);

        var type = await MatchAircraftAsync(flight.AircraftTitle, ct);
        // "Right aircraft" bonus: passenger charters need SEATS for everyone; cargo needs useful LOAD.
        bool payloadMatched = a.Type.CarriesPassengers()
            ? type?.Seats is int seats && seats >= a.Pax
            : type?.UsefulLoadLbs is int usefulLoad && usefulLoad >= a.WeightLbs;
        int xp = a.XpQuote + (payloadMatched ? (int)Math.Round(a.XpQuote * _cfg.PayloadMatchXpBonusPct) : 0);
        if (flight.Scored) // a great flight grows the pilot faster; a poor one slower
            xp = (int)Math.Round(xp * _cfg.ScoreXpMultiplier(flight.OverallScore));

        var jobRef = a.JobId.ToString();
        var postings = new List<LedgerPosting>();
        var lines = new List<PayoutLine>();
        if (baseCents > 0)
        {
            postings.Add(new(LedgerCategory.JobPayout, baseCents / 100m, $"{a.Type} payout to {a.DestIcao}",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:base"));
            lines.Add(new(outcome.Grade == MissionGrade.Full ? "Base reward" : $"Base reward ({outcome.Reason})", baseCents));
        }

        if (landingDelta > 0)
        {
            postings.Add(new(LedgerCategory.JobBonus, landingDelta / 100m, "Smooth landing bonus",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:landing"));
            lines.Add(new($"Landing bonus ({landingQualifier})", landingDelta));
        }
        else if (landingDelta < 0)
        {
            postings.Add(new(LedgerCategory.Penalty, landingDelta / 100m, "Hard-landing penalty",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:landing"));
            lines.Add(new($"Landing penalty ({landingQualifier})", landingDelta));
        }

        // A loyal client pays a repeat premium on top of the reward (Phase 8d) — the demand-side reward for a
        // relationship you've kept. Never negative, so you always still get at least the quoted base.
        if (loyaltyBonus > 0)
        {
            postings.Add(new(LedgerCategory.JobBonus, loyaltyBonus / 100m, $"Loyal client bonus — {client!.Name}",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:loyalty"));
            lines.Add(new($"Loyal client bonus ({client!.Name})", loyaltyBonus));
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

        // Fuel actually burned is a real running cost for a leg flown in an owned airframe (Phase 7e) —
        // the FuelUsedLbs the tracker already recorded, finally priced. A leg flown outside your fleet
        // (no owned instance) isn't tracked, so it carries no fuel charge.
        long grossFuel = aircraftInstanceId is not null && flight.FuelUsedLbs > 0
            ? (long)Math.Round(flight.FuelUsedLbs * _cfg.FuelPriceCentsPerLb)
            : 0;
        // A fuel farm at the departure base sells you fuel wholesale — discount this leg's burn (Phase 7g).
        long fuelCost = grossFuel;
        if (grossFuel > 0)
        {
            int farm = await _db.Bases
                .Where(b => b.CompanyId == a.AccountId && b.AirportIcao == a.OriginIcao && b.IsActive && !b.IsDeleted)
                .Select(b => (int?)b.FuelFarmLevel).FirstOrDefaultAsync(ct) ?? 0;
            double disc = _cfg.FuelFarmDiscountPct(farm);
            if (disc > 0)
                fuelCost = (long)Math.Round(grossFuel * (1 - disc));
        }
        if (fuelCost > 0)
        {
            bool farmRate = fuelCost < grossFuel;
            postings.Add(new(LedgerCategory.Fuel, -(fuelCost / 100m), farmRate ? $"Fuel — {flight.FuelUsedLbs:F0} lb (farm rate)" : $"Fuel — {flight.FuelUsedLbs:F0} lb",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:fuel"));
            lines.Add(new(farmRate ? $"Fuel ({flight.FuelUsedLbs:F0} lb, farm rate)" : $"Fuel ({flight.FuelUsedLbs:F0} lb)", -fuelCost));
        }

        long total = baseCents + landingDelta + loyaltyBonus - landingFee - fuelCost;
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
            // Phase 7b/7c — record the scored assessment, but only for a leg the tracker actually graded;
            // a manual/legacy record stays "not scored" (null) rather than showing a misleading default.
            TouchdownFpmWorst3 = flight.Scored ? flight.TouchdownFpmWorst3 : null,
            TouchdownG = flight.Scored ? flight.TouchdownG : null,
            LandingScore = flight.Scored ? flight.LandingScore : null,
            ApproachScore = flight.Scored ? flight.ApproachScore : null,
            OverallScore = flight.Scored ? flight.OverallScore : null,
            StabilizedApproach = flight.Scored ? flight.StabilizedApproach : null,
            ViolationPoints = flight.Scored ? flight.ViolationPoints : null,
            ScoreValid = flight.Scored ? flight.ScoreValid : null,
            // Phase 7d — the delivery grade, stored only when the type had a real completion rule (else null).
            OutcomeGrade = outcome.Grade == MissionGrade.Full ? (int?)null : (int)outcome.Grade,
            OutcomeReason = outcome.Reason,
        };
        _db.Flights.Add(flightEntity);

        // Persist the real scored events the tracker produced, in THIS same transaction (Phase 7a).
        // These are the very moments streamed live during the leg; keeping them lets the logbook replay
        // the running story of a flight instead of the client fabricating one after the fact.
        int evSeq = 0;
        foreach (var ev in flight.Events)
        {
            _db.FlightEvents.Add(new FlightEventRecord
            {
                FlightId = flightEntity.Id,
                Seq = evSeq++,
                At = ev.At,
                Severity = ev.Severity.ToString(),
                Message = ev.Message,
            });
        }

        pilot.Xp += xp;
        // Promotion (Phase 3a): XP is cumulative, so recompute the rank and note a crossing to celebrate.
        var earnedRank = RankTiers.ForXp(pilot.Xp);
        PilotRank? promotedTo = earnedRank > pilot.Rank ? earnedRank : null;
        pilot.Rank = earnedRank;

        // Reputation (Phase 3f): a delivery nudges reputation by the mission's reward (Illicit is
        // negative), clamped to [0, 100.0] and logged so the drift stays legible.
        int repDelta = (outcome.Grade == MissionGrade.Failed
                          ? _cfg.FailedDeliveryReputationMilli                        // a failed delivery hurts
                          : MissionCatalog.TryDef(a.Type)?.ReputationMilliReward ?? 0) // else the mission's reward
                     + (flight.Scored ? _cfg.ScoreReputationMilli(flight.OverallScore, flight.ScoreValid) : 0);
        if (repDelta != 0)
        {
            int before = pilot.ReputationMilli;
            int after = Math.Clamp(before + repDelta, 0, 100_000);
            if (after != before)
            {
                pilot.ReputationMilli = after;
                _db.ReputationEvents.Add(new ReputationEvent
                {
                    PilotId = pilot.Id, DeltaMilli = after - before, BalanceMilli = after,
                    Reason = $"{a.Type} delivery to {a.DestIcao}", At = now,
                });
            }
        }

        // Move the client bond (Phase 8d): a clean delivery builds loyalty (a sharper flight a little more), a
        // partial dings it, a failure sours it. Applied once, in this same transaction, after the premium above
        // has already been paid from the pre-delivery loyalty.
        if (client is not null)
        {
            int loyaltyDelta = _cfg.ClientLoyaltyDeltaMilli(outcome.Grade, flight.Scored, flight.Scored ? flight.OverallScore : 0);
            client.LoyaltyMilli = Math.Clamp(client.LoyaltyMilli + loyaltyDelta, 0, EconomyConfig.LoyaltyMax);
            if (outcome.Grade == MissionGrade.Failed) client.JobsFailed++;
            else client.JobsCompleted++;
            client.LastJobAt = now;
            client.UpdatedAt = now;
        }

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

                // Wear: hull + engine drop with hours; the touchdown adds hull wear. A tracker-flown leg
                // wears continuously with the worst-of-three sink rate and peak g (Phase 7c); a manual
                // record keeps the old binary hard-landing step.
                int hourWear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour);
                int landingWear = flight.Scored
                    ? _cfg.LandingWearMilli(flight.TouchdownFpmWorst3, flight.TouchdownG)
                    : (_cfg.LandingModifierPct(flight.TouchdownFpm) < 0 ? _cfg.HardLandingWearMilli : 0);
                int hullWear = hourWear + landingWear;
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

        return new SettlementResult(flightEntity.Id, total, xp, payloadMatched, promotedTo, breakdown);
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
