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
        var type = await MatchAircraftAsync(flight.AircraftTitle, ct);

        // Mission completion (Phase 7d): judge the DELIVERY against this mission type's own rules — a firm
        // arrival damages fragile freight, a rough flight loses a VIP client. This scales (or, on a Failed
        // grade, zeroes) the base reward before anything else. Ordinary jobs grade Full and are unchanged.
        var outcome = MissionProfiles.Evaluate(a.Type, flight, a.DeadlineAt);
        long baseCents = outcome.Grade == MissionGrade.Failed
            ? 0
            : (long)Math.Round((decimal)grossBase * outcome.QualityMilli / 100_000m, MidpointRounding.AwayFromZero);

        // Load check (Phase 13, L9 warn-don't-block): if the sim reported weights and the job's cargo/passengers
        // clearly weren't loaded, pay less — you didn't really carry it. Empty ≈ MaxGross − useful load, so the
        // actual payload ≈ liftoff total − empty − fuel; we only dock when it's well below what the job needed.
        // All weights default 0 (sim didn't report) → the check is skipped and pay is unchanged (L10).
        bool underloaded = false;
        if (baseCents > 0 && flight.LiftoffMaxGrossLbs > 0 && flight.LiftoffTotalWeightLbs > 0
            && type?.UsefulLoadLbs is int ul && ul > 0)
        {
            double empty = flight.LiftoffMaxGrossLbs - ul;
            double loadedPayload = flight.LiftoffTotalWeightLbs - empty - flight.LiftoffFuelLbs;
            double expected = a.Type.CarriesPassengers() ? a.Pax * (double)_cfg.PaxWeightLbs : a.WeightLbs;
            if (expected > 0 && loadedPayload < expected * _cfg.UnderloadFloorFactor)
            {
                underloaded = true;
                baseCents = (long)Math.Round(baseCents * (decimal)_cfg.UnderloadedPayFactor, MidpointRounding.AwayFromZero);
            }
        }

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
        // Loyalty cools while you don't fly for a client (Phase 8d-2): decay the stored figure to now before
        // reading it, so a neglected client's premium has faded and this delivery re-anchors the bond.
        int loyaltyNow = client is null ? 0
            : _cfg.DecayedLoyaltyMilli(client.LoyaltyMilli, now - (client.LastJobAt ?? client.UpdatedAt));
        // The premium rides on the EARNED base (a failed/partial delivery earns a smaller tip too), computed
        // from pre-delivery loyalty so it rewards the history, not this leg. A CHEATED (voided-score) flight
        // forfeits it, exactly like the landing and comfort bonuses below — a bonus is never paid on a fraud.
        bool voided = flight.Scored && !flight.ScoreValid;
        long loyaltyBonus = client is not null && baseCents > 0 && !voided
            ? (long)Math.Round(baseCents * (decimal)_cfg.ClientLoyaltyBonusPct(loyaltyNow), MidpointRounding.AwayFromZero)
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
            string baseLabel = outcome.Grade == MissionGrade.Full ? "Base reward" : $"Base reward ({outcome.Reason})";
            if (underloaded) baseLabel += " · underloaded −" + Math.Round((1 - _cfg.UnderloadedPayFactor) * 100) + "%";
            lines.Add(new(baseLabel, baseCents));
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

        // Passenger comfort tip (Phase 10c): a smooth PASSENGER ride earns a bonus on the EARNED base — the
        // reward-side mirror of the 7d ride-quality penalty. Only a scored, valid, paid passenger leg qualifies
        // (a cheated flight forfeits it, like the landing bonus), and a rough ride simply earns no tip — base
        // pay always stands (L9). Never negative.
        long comfortBonus = a.Type.CarriesPassengers() && flight.Scored && flight.ScoreValid && baseCents > 0
            ? (long)Math.Round((decimal)baseCents * (decimal)_cfg.ComfortBonusPct(flight.ComfortScore), MidpointRounding.AwayFromZero)
            : 0;
        if (comfortBonus > 0)
        {
            postings.Add(new(LedgerCategory.JobBonus, comfortBonus / 100m, $"Smooth ride bonus (comfort {flight.ComfortScore})",
                LedgerRefType.Job, jobRef, DedupeKey: $"settle:{a.Id}:comfort"));
            lines.Add(new($"Comfort bonus (ride {flight.ComfortScore})", comfortBonus));
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

        long total = baseCents + landingDelta + loyaltyBonus + comfortBonus - landingFee - fuelCost;
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
            ComfortScore = flight.Scored ? flight.ComfortScore : null,
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

        // Airline operating reputation (Phase 11a): the leg you flew YOURSELF moves the airline's own name by
        // its telemetry score — a great leg builds it, a poor one dings it, a cheated one costs the most, and an
        // ordinary (coaching-band) or unscored leg leaves it untouched (L9/L10). A SEPARATE figure from the pilot
        // reputation above, on the owning Company, so autonomous crew competence never leaks into the pilot gate.
        // Applied in THIS same settlement transaction, guarded by the once-only Status==Settled gate below.
        int airRepDelta = _cfg.OperatingRepPlayerDeltaMilli(
            flight.OverallScore, flight.Scored, flight.Scored && flight.ScoreValid);
        if (airRepDelta != 0)
        {
            var company = await _db.Companies.FirstAsync(c => c.Id == a.AccountId, ct);
            int beforeAir = company.OperatingReputationMilli;
            int afterAir = Math.Clamp(beforeAir + airRepDelta, 0, EconomyConfig.OperatingReputationMax);
            if (afterAir != beforeAir)
            {
                company.OperatingReputationMilli = afterAir;
                _db.AirlineReputationEvents.Add(new AirlineReputationEvent
                {
                    CompanyId = a.AccountId, DeltaMilli = afterAir - beforeAir, BalanceMilli = afterAir,
                    Source = AirlineRepSource.Player,
                    Reason = $"{a.Type} to {a.DestIcao}, score {flight.OverallScore}", At = now,
                });
            }
        }

        // Move the client bond (Phase 8d): a clean delivery builds loyalty (a sharper flight a little more), a
        // partial dings it, a failure sours it. Applied once, in this same transaction, after the premium above
        // has already been paid from the pre-delivery loyalty.
        if (client is not null)
        {
            // Apply this delivery's move to the DECAYED loyalty (re-anchoring at now), so neglect between jobs
            // genuinely erodes the bond rather than being frozen at its old peak.
            int loyaltyDelta = _cfg.ClientLoyaltyDeltaMilli(outcome.Grade, flight.Scored, flight.Scored ? flight.OverallScore : 0)
                // A passenger client also remembers the RIDE (Phase 10c): a smooth flight builds the bond a
                // little more, a rough one sheds some — beyond the delivery grade the base delta already reflects.
                // Gated on ScoreValid to match the comfort TIP: a cheated leg forfeits the comfort loyalty too.
                + (a.Type.CarriesPassengers() && flight.Scored && flight.ScoreValid ? _cfg.ComfortLoyaltyDeltaMilli(flight.ComfortScore) : 0);
            client.LoyaltyMilli = Math.Clamp(loyaltyNow + loyaltyDelta, 0, EconomyConfig.LoyaltyMax);
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
                // Engine abuse (Phase 9e): the sim's own damage the leg accrued becomes extra engine-condition
                // loss — on top of the hours — which then bills through the existing maintenance + resale paths
                // (never a per-exceedance cash hit). Zero for a clean leg, or one the sim didn't instrument (L10).
                int engineAbuseWear = _cfg.EngineAbuseWearMilli(flight.EngineDamagePctAccrued);
                // Phase 13 (L9): cap what a SINGLE leg can strip, so one rough flight can't total an airframe —
                // wear is real and accumulates, but never a one-shot write-off.
                int hullLoss = Math.Min(hullWear, _cfg.MaxConditionLossPerLegMilli);
                int engineLoss = Math.Min(hourWear + engineAbuseWear, _cfg.MaxConditionLossPerLegMilli);
                instance.HullConditionMilli = Math.Max(0, instance.HullConditionMilli - hullLoss);
                instance.EngineConditionMilli = Math.Max(0, instance.EngineConditionMilli - engineLoss);
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
