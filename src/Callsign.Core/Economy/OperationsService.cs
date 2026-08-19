using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A hireable pilot preview. Its wage is economy-set (from a deterministic roll), never player-set.</summary>
public sealed record StaffCandidate(int Seed, string Name, long WagePerDayCents, int SkillMilli);

/// <summary>What a reconcile produced (for the reopen digest).</summary>
public sealed record ReconcileDigest(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long LoanCents, long InsuranceCents, long NetCents, int Incidents, IReadOnlyList<string> Grounded, IReadOnlyList<string> DutyMaxed, int EmptyLegs = 0);

/// <summary>
/// Staff + standing orders (Phase 2d): hire pilots, set repeating autonomous routes, and reconcile the
/// trips + wages that accrued while the app was closed. Everything moves through the ledger, so the
/// autonomous economy reconciles exactly like the hand-flown one.
/// </summary>
public sealed class OperationsService
{
    private static readonly string[] First = ["Amelia", "Jack", "Nadia", "Owen", "Priya", "Sven", "Lena", "Marco", "Yuki", "Diego", "Ingrid", "Tariq"];
    private static readonly string[] Last = ["Hart", "Boone", "Vance", "Okafor", "Rossi", "Lindqvist", "Nowak", "Haruna", "Silva", "Meyer", "Kaur", "Petrov"];

    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public OperationsService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    /// <summary>A deterministic slate of hireable pilots for a seed.</summary>
    public IReadOnlyList<StaffCandidate> GenerateCandidates(int seed, int count = 5)
    {
        var rng = new Random(seed);
        var list = new List<StaffCandidate>(count);
        for (int i = 0; i < count; i++)
            list.Add(MakeCandidate(rng.Next()));
        return list;
    }

    private static StaffCandidate MakeCandidate(int seed)
    {
        var r = new Random(seed);
        var name = $"{First[r.Next(First.Length)]} {Last[r.Next(Last.Length)]}";
        int skill = 30_000 + r.Next(60_001);                 // 30%..90%
        long wage = 8_000 + skill / 1000L * 400;             // ~$200 (green) .. ~$440/day (ace)
        return new StaffCandidate(seed, name, wage, skill);
    }

    /// <summary>Hire the candidate identified by its seed (regenerated server-side, so the wage is trusted).</summary>
    public async Task<Staff> HireAsync(Guid companyId, int candidateSeed, CancellationToken ct = default)
    {
        var c = MakeCandidate(candidateSeed);
        var now = _clock.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Name = c.Name, Role = StaffRole.Pilot,
            WagePerDayCents = c.WagePerDayCents, SkillMilli = c.SkillMilli,
            HiredAt = now, LastPaidAt = now, IsActive = true, UpdatedAt = now,
        };
        _db.Staff.Add(staff);
        await _db.SaveChangesAsync(ct);
        return staff;
    }

    /// <summary>True if a pilot already crews an active standing order or route — one crew flies one line (Phase 7f).</summary>
    public async Task<bool> CrewAlreadyFlyingAsync(Guid staffId, CancellationToken ct = default)
        => await _db.StandingOrders.AnyAsync(o => o.StaffId == staffId && o.IsActive && !o.IsDeleted, ct)
        || await _db.Routes.AnyAsync(r => r.StaffId == staffId && r.Active && !r.IsDeleted, ct);

    /// <summary>Assign a pilot + an owned aircraft to a repeating route (its reward frozen at economy price).</summary>
    public async Task<StandingOrder> CreateStandingOrderAsync(
        Guid companyId, Guid staffId, Guid aircraftId, string destIcao, int priceMultiplierMilli = 1000, CancellationToken ct = default)
    {
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftId && a.CompanyId == companyId, ct)
                       ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (aircraft.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("That aircraft isn't available.");
        // One pilot flies one line: without this a single crew stacked on two lines would fly the daily duty
        // limit on EACH, defeating the FTL cap (Phase 7f). Assign a different pilot — or hire one.
        if (await CrewAlreadyFlyingAsync(staffId, ct))
            throw new InvalidOperationException($"{staff.Name} already flies another route — assign a different pilot or hire one.");

        var origin = aircraft.LocationIcao;
        var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == origin, ct)
                   ?? throw new InvalidOperationException($"Origin {origin} is unknown.");
        var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                   ?? throw new InvalidOperationException($"Destination {destIcao} is unknown.");

        double dist = GeoMath.DistanceNm(oAir.Latitude, oAir.Longitude, dAir.Latitude, dAir.Longitude);
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct);
        // Type rating: a hired pilot must be experienced enough for the aircraft's category (Phase 7f).
        int need = _cfg.MinSkillMilliForCategory(type?.Category ?? AircraftCategory.Unknown);
        if (staff.SkillMilli < need)
            throw new InvalidOperationException($"{staff.Name} ({staff.SkillMilli / 1000}%) isn't rated for the {type!.Category} — assign a pilot at {need / 1000}%+.");
        double cruise = type?.CruiseKtas ?? 150;
        double rtHours = 2 * dist / Math.Max(60, cruise);
        int weight = Math.Min(type?.UsefulLoadLbs ?? 1_000, 1_000);
        long reward = _cfg.CargoRewardCents(dist, weight); // economy-frozen FAIR rate; your markup rides on top
        int markup = Math.Clamp(priceMultiplierMilli, 1000, _cfg.MaxContractMarkupMilli);

        var now = _clock.UtcNow;
        var order = new StandingOrder
        {
            Id = Guid.NewGuid(), CompanyId = companyId, StaffId = staffId, AircraftInstanceId = aircraftId,
            OriginIcao = origin, DestIcao = destIcao, DistanceNm = dist, RoundTripHours = rtHours,
            RewardPerTripCents = reward, PriceMultiplierMilli = markup, Commodity = "General freight", WeightLbs = weight,
            IsActive = true, StartedAt = now, LastReconciledAt = now, UpdatedAt = now,
        };
        _db.StandingOrders.Add(order);
        aircraft.Availability = AircraftAvailability.Reserved; // held by the standing order
        aircraft.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>
    /// Re-price an active standing order. Reconciles first so trips already flown are booked at the OLD price
    /// (the new markup only ever applies to future trips — no retroactive re-pricing). Returns the clamped
    /// markup actually stored.
    /// </summary>
    public async Task<int> SetOrderPriceAsync(Guid companyId, Guid orderId, int priceMultiplierMilli, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book pending trips at the current price before it changes
        var order = await _db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && o.IsActive && !o.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Standing order not found.");
        int markup = Math.Clamp(priceMultiplierMilli, 1000, _cfg.MaxContractMarkupMilli);
        order.PriceMultiplierMilli = markup;
        order.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return markup;
    }

    /// <summary>
    /// Re-price an active route. Reconciles first so trips already flown book at the OLD price (the new markup
    /// applies to future trips only). Returns the clamped markup actually stored.
    /// </summary>
    public async Task<int> SetRoutePriceAsync(Guid companyId, Guid routeId, int priceMultiplierMilli, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book pending trips at the current price before it changes
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.CompanyId == companyId && r.Active && !r.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Route not found.");
        int markup = Math.Clamp(priceMultiplierMilli, 1000, _cfg.MaxContractMarkupMilli);
        route.PriceMultiplierMilli = markup;
        route.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return markup;
    }

    /// <summary>Cancel a standing order and free its aircraft.</summary>
    public async Task CancelStandingOrderAsync(Guid companyId, Guid orderId, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book any trips flown up to now first
        var order = await _db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order is null || !order.IsActive) return;
        order.IsActive = false;
        order.UpdatedAt = _clock.UtcNow;
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == order.AircraftInstanceId, ct);
        if (aircraft is not null) { aircraft.Availability = AircraftAvailability.Available; aircraft.UpdatedAt = _clock.UtcNow; }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Book the autonomous trips + wages that accrued since the last reconcile. Deterministic in the
    /// clock: N trips = floor(elapsed / round-trip time). Everything posts to the ledger, so cash always
    /// reconciles. Idempotent — the watermark only advances by whole booked trips.
    /// </summary>
    public async Task<ReconcileDigest> ReconcileAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        int totalTrips = 0;
        long grossIncome = 0, totalFees = 0, totalWages = 0, totalRent = 0, totalLoan = 0, totalInsurance = 0;
        int totalIncidents = 0;            // trips a crew botched — skill is what keeps this down (Phase 7f)
        int totalEmpty = 0;                // legs that flew empty because a marked-up line priced out the client (Phase 7g)
        var grounded = new List<string>(); // tails that couldn't fly their autonomous work — surfaced in the digest
        var dutyMaxed = new List<string>();// tails whose lone crew hit the duty limit — hire more crew to fly them harder

        // Airports where we own a base — landings there are fee-free.
        var baseIcaos = (await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .Select(b => b.AirportIcao).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var o in await _db.StandingOrders.Where(o => o.CompanyId == companyId && o.IsActive && !o.IsDeleted).ToListAsync(ct))
        {
            double elapsedH = (now - o.LastReconciledAt).TotalHours;
            int rawTrips = o.RoundTripHours > 0 ? (int)Math.Floor(elapsedH / o.RoundTripHours) : 0;
            if (rawTrips <= 0)
                continue;

            // A grounded tail can't fly: hold the order (its clock doesn't advance, so the trips resume once
            // serviced) and warn the player in the digest — they never reopen to a silently idle order (Law 4).
            var soAircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == o.AircraftInstanceId, ct);
            if (soAircraft is not null && AircraftDealerService.AirworthinessOf(soAircraft, _cfg, now) is { Airworthy: false } soAw)
            {
                grounded.Add($"{soAircraft.Tail} — {soAw.Reason}");
                continue;
            }

            // FTL: the lone crew can only fly so many duty hours per day. Cap the trips to that — the rest of
            // the window is rest, not deferred flying — so one pilot can't run a tail round the clock.
            int soMaxTrips = o.RoundTripHours > 0 ? (int)Math.Floor(_cfg.MaxDutyHoursPerDay / 24.0 * elapsedH / o.RoundTripHours) : 0;
            int trips = Math.Min(rawTrips, soMaxTrips);
            bool dutyCapped = trips < rawTrips;
            if (trips <= 0)
                continue; // not enough duty accrued yet — the crew rests until the next trip is legal

            var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == o.OriginIcao, ct);
            var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == o.DestIcao, ct);
            long feePerTrip = (oAir is not null && !baseIcaos.Contains(o.OriginIcao) ? _cfg.LandingFeeCents(oAir.Kind) : 0)
                            + (dAir is not null && !baseIcaos.Contains(o.DestIcao) ? _cfg.LandingFeeCents(dAir.Kind) : 0);
            // The crew flies each trip: their skill sets how often a trip is botched (a diversion — half pay).
            var soCrew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == o.StaffId, ct);
            var soRoll = RollTrips(_cfg, o.Id, o.LastReconciledAt.UtcTicks, trips, soCrew?.SkillMilli ?? 50_000, o.RewardPerTripCents, o.PriceMultiplierMilli);
            long income = soRoll.Income;
            long fees = trips * feePerTrip;
            var stamp = o.LastReconciledAt.UtcTicks;

            var postings = new List<LedgerPosting>
            {
                new(LedgerCategory.JobPayout, income / 100m, $"Standing order {o.OriginIcao}↔{o.DestIcao} ×{trips}",
                    AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}"),
            };
            if (fees > 0)
                postings.Add(new(LedgerCategory.AirportFee, -(fees / 100m), $"Landing fees {o.OriginIcao}↔{o.DestIcao} ×{trips}",
                    AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}:fee"));
            await _ledger.StageBatchAsync(companyId, postings, ct);

            var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == o.AircraftInstanceId, ct);
            if (aircraft is not null)
            {
                double hours = trips * o.RoundTripHours;
                aircraft.AirframeHours += hours;
                int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + soRoll.ExtraWearMilli;
                aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                aircraft.UpdatedAt = now;
            }

            // Duty-capped: consume the whole window (the excess was rest); else advance by whole trips only.
            o.LastReconciledAt = dutyCapped ? now : o.LastReconciledAt.AddHours(trips * o.RoundTripHours);
            o.UpdatedAt = now;
            totalTrips += trips;
            grossIncome += income;
            totalFees += fees;
            totalIncidents += soRoll.Incidents;
            totalEmpty += soRoll.Empty;
            if (dutyCapped) dutyMaxed.Add(soAircraft?.Tail ?? $"{o.OriginIcao}↔{o.DestIcao}");
            SharpenCrew(soCrew, trips, now);
        }

        foreach (var s in await _db.Staff.Where(s => s.CompanyId == companyId && s.IsActive && !s.IsDeleted).ToListAsync(ct))
        {
            double days = (now - s.LastPaidAt).TotalDays;
            long wage = (long)Math.Round(days * s.WagePerDayCents);
            if (wage <= 0)
                continue;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.StaffWage, -(wage / 100m), $"Wages — {s.Name}",
                    StaffId: s.Id, DedupeKey: $"wage:{s.Id}:{s.LastPaidAt.UtcTicks}"),
            }, ct);
            s.LastPaidAt = now;
            s.UpdatedAt = now;
            totalWages += wage;
        }

        foreach (var b in await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted).ToListAsync(ct))
        {
            double days = (now - b.LastRentBilledAt).TotalDays;
            long rent = (long)Math.Round(days * b.RentPerDayCents);
            long upkeep = (long)Math.Round(days * _cfg.MaintenanceShopUpkeepCentsPerDay(b.MaintenanceLevel)); // shop running cost
            long charge = rent + upkeep;
            if (charge <= 0)
                continue;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.BaseRent, -(charge / 100m),
                    upkeep > 0 ? $"Base costs — {b.AirportIcao}" : $"Base rent — {b.AirportIcao}",
                    BaseId: b.Id, DedupeKey: $"rent:{b.Id}:{b.LastRentBilledAt.UtcTicks}"),
            }, ct);
            b.LastRentBilledAt = now;
            b.UpdatedAt = now;
            totalRent += charge;
        }

        // Loans (Phase 4a): bill accrued interest + straight-line principal over whole elapsed days.
        foreach (var loan in await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted).ToListAsync(ct))
        {
            int days = (int)Math.Floor((now - loan.PaymentLastBilledAt).TotalDays);
            if (days <= 0)
                continue;
            var (interest, principal) = LoanCatalog.Amortize(loan.OutstandingCents, loan.PrincipalCents, loan.AprBps, loan.TermDays, days);
            var stamp = loan.PaymentLastBilledAt.UtcTicks;
            var lp = new List<LedgerPosting>();
            if (interest > 0)
                lp.Add(new(LedgerCategory.LoanInterest, -(interest / 100m), $"Loan interest — tier {loan.Tier}",
                    LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-int:{loan.Id}:{stamp}"));
            if (principal > 0)
                lp.Add(new(LedgerCategory.LoanPayment, -(principal / 100m), $"Loan repayment — tier {loan.Tier}",
                    LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-pay:{loan.Id}:{stamp}"));
            if (lp.Count > 0)
                await _ledger.StageBatchAsync(companyId, lp, ct);

            loan.OutstandingCents = Math.Max(0, loan.OutstandingCents - principal);
            loan.PaymentLastBilledAt = loan.PaymentLastBilledAt.AddDays(days); // advance by whole billed days only
            if (loan.OutstandingCents == 0)
                loan.Status = LoanStatus.PaidOff;
            loan.UpdatedAt = now;
            totalLoan += interest + principal;
        }

        // Routes (Phase 4d): base-to-base scheduled trips — fee-free (both ends are your bases).
        foreach (var route in await _db.Routes.Where(r => r.CompanyId == companyId && r.Active && !r.IsDeleted).ToListAsync(ct))
        {
            double elapsedH = (now - route.LastReconciledAt).TotalHours;
            int rawTrips = route.RoundTripHours > 0 ? (int)Math.Floor(elapsedH / route.RoundTripHours) : 0;
            if (rawTrips <= 0)
                continue;

            var rtAircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == route.AircraftInstanceId, ct);
            if (rtAircraft is not null && AircraftDealerService.AirworthinessOf(rtAircraft, _cfg, now) is { Airworthy: false } rtAw)
            {
                grounded.Add($"{rtAircraft.Tail} — {rtAw.Reason}");
                continue; // held until serviced (Law 4)
            }

            int rtMaxTrips = route.RoundTripHours > 0 ? (int)Math.Floor(_cfg.MaxDutyHoursPerDay / 24.0 * elapsedH / route.RoundTripHours) : 0;
            int trips = Math.Min(rawTrips, rtMaxTrips);
            bool dutyCapped = trips < rawTrips;
            if (trips <= 0)
                continue; // duty not yet accrued — the crew rests

            var rtCrew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == route.StaffId, ct);
            var rtRoll = RollTrips(_cfg, route.Id, route.LastReconciledAt.UtcTicks, trips, rtCrew?.SkillMilli ?? 50_000, route.RewardPerTripCents, route.PriceMultiplierMilli);
            long income = rtRoll.Income;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.JobPayout, income / 100m, $"Route {route.Name} ×{trips}",
                    AircraftInstanceId: route.AircraftInstanceId, DedupeKey: $"route:{route.Id}:{route.LastReconciledAt.UtcTicks}"),
            }, ct);

            var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == route.AircraftInstanceId, ct);
            if (aircraft is not null)
            {
                double hours = trips * route.RoundTripHours;
                aircraft.AirframeHours += hours;
                int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + rtRoll.ExtraWearMilli;
                aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                aircraft.UpdatedAt = now;
            }
            route.LastReconciledAt = dutyCapped ? now : route.LastReconciledAt.AddHours(trips * route.RoundTripHours);
            route.UpdatedAt = now;
            totalTrips += trips;
            grossIncome += income;
            totalIncidents += rtRoll.Incidents;
            totalEmpty += rtRoll.Empty;
            if (dutyCapped) dutyMaxed.Add(rtAircraft?.Tail ?? route.Name);
            SharpenCrew(rtCrew, trips, now);
        }

        // Insurance premiums (Phase 4c): the running cost of coverage, prorated by whole days.
        foreach (var policy in await _db.InsurancePolicies.Where(p => p.CompanyId == companyId && p.Active && !p.IsDeleted).ToListAsync(ct))
        {
            int days = (int)Math.Floor((now - policy.PremiumLastBilledAt).TotalDays);
            if (days <= 0)
                continue;
            long premium = InsuranceService.PremiumForDays(policy.PremiumPerWeekCents, days);
            if (premium > 0)
                await _ledger.StageBatchAsync(companyId, new[]
                {
                    new LedgerPosting(LedgerCategory.InsurancePremium, -(premium / 100m), "Insurance premium",
                        LedgerRefType.InsuranceClaim, policy.Id.ToString(), DedupeKey: $"ins-prem:{policy.Id}:{policy.PremiumLastBilledAt.UtcTicks}"),
                }, ct);
            policy.PremiumLastBilledAt = policy.PremiumLastBilledAt.AddDays(days);
            policy.UpdatedAt = now;
            totalInsurance += premium;
        }

        await _db.SaveChangesAsync(ct);
        return new ReconcileDigest(totalTrips, grossIncome, totalFees, totalWages, totalRent, totalLoan, totalInsurance,
            grossIncome - totalFees - totalWages - totalRent - totalLoan - totalInsurance, totalIncidents, grounded, dutyMaxed, totalEmpty);
    }

    /// <summary>
    /// Fly a batch of autonomous trips, letting the crew's skill decide how many go wrong. Each trip rolls a
    /// deterministic incident (seeded from the order id + trip ordinal + the reconcile watermark, so a retry
    /// reproduces the SAME result and the ledger stays idempotent). p(incident) = base·(1−skill)^exp, so an
    /// ace almost never botches a trip and a green pilot botches many; each incident then lands on a severity
    /// tier — mostly a minor scuff, sometimes a diversion (half pay), rarely a lost trip. Fatigue is later.
    /// </summary>
    // Decorrelates the demand (did-the-client-ship) roll from the incident roll so a marked-up line's empty
    // legs never perturb which trips get botched. Any fixed odd constant does; the seed still folds in the
    // order id + trip ordinal + watermark, so it stays deterministic and idempotent.
    private const long FillSeedSalt = 0x5DEECE66D;

    internal static (long Income, int Incidents, int ExtraWearMilli, int Empty) RollTrips(
        EconomyConfig cfg, Guid id, long stampTicks, int trips, int skillMilli, long rewardPerTrip,
        int priceMultiplierMilli = 1000)
    {
        long effReward = (long)Math.Round(rewardPerTrip * (priceMultiplierMilli / 1000.0)); // your marked price
        double pFill = cfg.ContractFillProbability(priceMultiplierMilli);
        double skillFrac = Math.Clamp(skillMilli / 100_000.0, 0, 1);
        double pIncident = cfg.BaseIncidentRatePct * Math.Pow(1 - skillFrac, cfg.IncidentSkillExponent);
        long income = 0;
        int incidents = 0, wear = 0, empty = 0;
        for (int i = 0; i < trips; i++)
        {
            // Did the client ship at your price? Above the fair rate some legs run empty — the aircraft still
            // flew (the caller bills its fuel, wear, fees and duty), it just earned nothing. A separate seed
            // stream keeps this independent of the incident roll; at the fair rate pFill==1, so nothing changes.
            if (pFill < 1.0)
            {
                var fillRng = new Random(StableSeed(id, i, stampTicks ^ FillSeedSalt));
                if (fillRng.NextDouble() >= pFill) { empty++; continue; }
            }

            var rng = new Random(StableSeed(id, i, stampTicks));
            if (rng.NextDouble() >= pIncident)
            {
                income += effReward; // clean trip
                continue;
            }
            incidents++;
            double severity = rng.NextDouble(); // which kind of incident
            if (severity < cfg.IncidentMajorShare)
            {
                wear += cfg.IncidentMajorWearMilli; // major — the trip is lost, no pay
            }
            else if (severity < cfg.IncidentMajorShare + cfg.IncidentDiversionShare)
            {
                income += (long)Math.Round(effReward * (1 - cfg.IncidentDiversionDockPct)); // diversion — half
                wear += cfg.IncidentDiversionWearMilli;
            }
            else
            {
                income += (long)Math.Round(effReward * (1 - cfg.IncidentMinorDockPct)); // minor scuff
                wear += cfg.IncidentMinorWearMilli;
            }
        }
        return (income, incidents, wear, empty);
    }

    // Experience sharpens a hired pilot: their skill drifts up with every trip flown, toward a ceiling
    // below perfect (Phase 7f). So a cheap green hire is an appreciating asset — and fewer incidents follow.
    private void SharpenCrew(Staff? crew, int trips, DateTimeOffset now)
    {
        if (crew is null || trips <= 0 || crew.SkillMilli >= _cfg.CrewSkillCeilingMilli)
            return;
        crew.SkillMilli = Math.Min(_cfg.CrewSkillCeilingMilli, crew.SkillMilli + _cfg.CrewProficiencyGainMilliPerTrip * trips);
        crew.UpdatedAt = now;
    }

    // A STABLE seed (unlike string/HashCode.Combine, which are per-process randomised) so reconcile is
    // deterministic across runs — the same window always rolls the same incidents.
    private static int StableSeed(Guid id, int i, long ticks)
    {
        unchecked
        {
            int h = id.GetHashCode();
            h = h * 397 + i;
            h = h * 397 + (int)(ticks ^ (ticks >> 32));
            return h;
        }
    }
}
