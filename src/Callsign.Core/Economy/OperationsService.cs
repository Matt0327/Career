using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A hireable pilot preview. Its wage is economy-set (from a deterministic roll), never player-set.</summary>
public sealed record StaffCandidate(int Seed, string Name, long WagePerDayCents, int SkillMilli);

/// <summary>What a reconcile produced (for the reopen digest).</summary>
public sealed record ReconcileDigest(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long LoanCents, long InsuranceCents, long NetCents, int Incidents, IReadOnlyList<string> Grounded, IReadOnlyList<string> DutyMaxed, int EmptyLegs = 0, IReadOnlyList<string>? LoanWarnings = null, IReadOnlyList<string>? Defaults = null, IReadOnlyList<string>? CertLapsed = null, int WeatheredOut = 0, IReadOnlyList<string>? CertExpiring = null, long RentalCents = 0, IReadOnlyList<string>? RentalsExpiring = null, IReadOnlyList<string>? RentalsAutoReturned = null, int OperatingRepDeltaMilli = 0);

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
        // Only an OWNED tail can fly autonomous work (Phase 9f): a rental/lease is hand-fly-only, which
        // structurally kills the rent-a-tail-for-autonomous-income pump.
        if (aircraft.Ownership != OwnershipKind.Owned)
            throw new InvalidOperationException("Only an aircraft you own can fly a standing order — a rental is hand-fly-only.");
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
        // Phase 11c — if this line departs one of MY bases, the airline's operating reputation lifts its frozen
        // per-trip pay (1.0× off-hub / at rep 0). Baked in here at creation, like the route above. Phase 11e — if
        // that base is an upgraded hub, its HubLevel amplifies the lift (0 = plain base, unchanged).
        var originBase = await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted && b.AirportIcao == origin)
            .Select(b => new { b.HubLevel }).FirstOrDefaultAsync(ct);
        int repMilli = originBase is not null
            ? await _db.Companies.Where(c => c.Id == companyId).Select(c => c.OperatingReputationMilli).FirstOrDefaultAsync(ct)
            : 0;
        long reward = (long)Math.Round(_cfg.CargoRewardCents(dist, weight) * _cfg.HubReputationPayFactor(repMilli, originBase?.HubLevel ?? 0)); // economy-frozen FAIR rate; your markup rides on top
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
        // A scheduled-passenger route (Phase 11f) has no markup lever — the frozen load factor IS its demand model,
        // so a markup would double-count it. Keep it fixed at the fair rate.
        if (route.SeatCapacity != null)
            throw new InvalidOperationException("Scheduled passenger service has no markup — the load factor is the demand.");
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
        int totalWeatheredOut = 0;         // autonomous trips scrubbed by foul weather at the origin (Phase 8f-2)
        var grounded = new List<string>(); // tails that couldn't fly their autonomous work — surfaced in the digest
        var dutyMaxed = new List<string>();// tails whose lone crew hit the duty limit — hire more crew to fly them harder
        var certLapsed = new List<string>();// routes held because their operating certificate lapsed (Phase 8e)
        var certExpiring = new List<string>();// certificates nearing expiry — renew before they lapse (Phase 8e-2)
        var world = new WorldOracle(_cfg); // the pure synthetic weather model (Phase 8f-2), for scrub checks

        // How many of a batch's trip slots are weathered out at the origin (Phase 8f-2): a pure, deterministic
        // count over the slot departure times. Neutral (0) when the origin airport is unknown, so a leg with no
        // geography flies as before — the same isolation the market-demand read uses.
        int WeatheredTrips(Airport? origin, DateTimeOffset from, int trips, double roundTripHours)
        {
            if (origin is null || trips <= 0 || roundTripHours <= 0) return 0;
            int scrubbed = 0;
            for (int i = 0; i < trips; i++)
            {
                var w = world.WeatherAt(origin.Latitude, origin.Longitude, from.AddHours(i * roundTripHours));
                if (_cfg.WeatherScrubsTrip(w.VisibilitySm, w.WindKts)) scrubbed++;
            }
            return scrubbed;
        }
        var loanWarnings = new List<string>(); // loans in forbearance — pay down before the grace runs out (Phase 7g)
        var defaults = new List<string>();  // loans that defaulted this pass — a charged-off, credit-wrecking event

        // Airports where we own a base — landings there are fee-free.
        var baseIcaos = (await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .Select(b => b.AirportIcao).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The one tracked company row (Phase 11a): StageBatchAsync mutates its cash on this same instance (EF
        // identity map), so the loan solvency check below reads postings-so-far, and we reuse it for the operating-
        // reputation move. repStartMilli is the name AS OF pass start, so summing crew pulls across batches is
        // order-independent and deterministic.
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId, ct);
        int repStartMilli = company.OperatingReputationMilli;
        long repPullRaw = 0;    // summed crew pull (milli); step-capped, then bounded to the target, before the terminal save
        double repFracSum = 0;  // summed convergence fraction — the denominator of the trip-weighted crew-skill target

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
            // Foul weather at the origin scrubs a share of the trips (Phase 8f-2): they don't fly (no income, no
            // fees, no wear), but the time still passes — the watermark advances over the whole window below.
            int scrubbed = WeatheredTrips(oAir, o.LastReconciledAt, trips, o.RoundTripHours);
            int flown = trips - scrubbed;
            long feePerTrip = (oAir is not null && !baseIcaos.Contains(o.OriginIcao) ? _cfg.LandingFeeCents(oAir.Kind) : 0)
                            + (dAir is not null && !baseIcaos.Contains(o.DestIcao) ? _cfg.LandingFeeCents(dAir.Kind) : 0);
            // The crew flies each trip: their skill sets how often a trip is botched (a diversion — half pay).
            var soCrew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == o.StaffId, ct);
            var soRoll = RollTrips(_cfg, o.Id, o.LastReconciledAt.UtcTicks, flown, soCrew?.SkillMilli ?? 50_000, o.RewardPerTripCents, o.PriceMultiplierMilli);
            long income = soRoll.Income;
            long fees = flown * feePerTrip;
            var stamp = o.LastReconciledAt.UtcTicks;

            if (flown > 0)
            {
                var postings = new List<LedgerPosting>
                {
                    new(LedgerCategory.JobPayout, income / 100m, $"Standing order {o.OriginIcao}↔{o.DestIcao} ×{flown}",
                        AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}"),
                };
                if (fees > 0)
                    postings.Add(new(LedgerCategory.AirportFee, -(fees / 100m), $"Landing fees {o.OriginIcao}↔{o.DestIcao} ×{flown}",
                        AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}:fee"));
                await _ledger.StageBatchAsync(companyId, postings, ct);

                var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == o.AircraftInstanceId, ct);
                if (aircraft is not null)
                {
                    double hours = flown * o.RoundTripHours;
                    aircraft.AirframeHours += hours;
                    int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + soRoll.ExtraWearMilli;
                    aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                    aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                    aircraft.UpdatedAt = now;
                }
            }

            // Duty-capped: consume the whole window (the excess was rest); else advance by whole trips only.
            o.LastReconciledAt = dutyCapped ? now : o.LastReconciledAt.AddHours(trips * o.RoundTripHours);
            o.UpdatedAt = now;
            totalTrips += flown;
            grossIncome += income;
            totalFees += fees;
            totalIncidents += soRoll.Incidents;
            totalEmpty += soRoll.Empty;
            totalWeatheredOut += scrubbed;
            if (dutyCapped) dutyMaxed.Add(soAircraft?.Tail ?? $"{o.OriginIcao}↔{o.DestIcao}");
            SharpenCrew(soCrew, flown, now);
            repPullRaw += _cfg.OperatingRepCrewPullMilli(repStartMilli, soCrew?.SkillMilli ?? 50_000, flown); // Phase 11a
            repFracSum += _cfg.OperatingRepConvergeFrac(flown);
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
            long upkeep = (long)Math.Round(days * (_cfg.MaintenanceShopUpkeepCentsPerDay(b.MaintenanceLevel)
                + _cfg.FuelFarmUpkeepCentsPerDay(b.FuelFarmLevel)
                + _cfg.HubUpkeepCentsPerDay(b.HubLevel))); // facilities running cost (shop + fuel farm + hub, Phase 11e)
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

        // Routes (Phase 4d): base-to-base scheduled trips — fee-free (both ends are your bases).
        var validCerts = await _db.OperatingCertificates
            .Where(c => c.CompanyId == companyId && c.ExpiresAt > now).ToListAsync(ct);
        var validCertKinds = validCerts.Select(c => c.Kind).ToHashSet();
        // Renewal nudge (Phase 8e-2): warn before a valid certificate lapses, so a gated route never silently
        // stops and you're never caught out (Law 4). Fires regardless of whether you run any routes.
        foreach (var c in validCerts)
        {
            int daysLeft = (int)Math.Ceiling((c.ExpiresAt - now).TotalDays);
            if (daysLeft <= _cfg.CertRenewalWarnDays)
                certExpiring.Add($"{CertificateCatalog.Def(c.Kind).DisplayName} in {daysLeft}d");
        }
        foreach (var route in await _db.Routes.Where(r => r.CompanyId == companyId && r.Active && !r.IsDeleted).ToListAsync(ct))
        {
            double elapsedH = (now - route.LastReconciledAt).TotalHours;
            int rawTrips = route.RoundTripHours > 0 ? (int)Math.Floor(elapsedH / route.RoundTripHours) : 0;
            if (rawTrips <= 0)
                continue;

            // Operating-certificate hold (Phase 8e): autonomous route income is only realised at reconcile, and
            // a route's authority is checked HERE, as of now. If the required certificate has lapsed, the route
            // pays nothing this pass and its whole unsettled window is forfeit — the watermark advances to now (as
            // the duty cap does), so a lapse can't be banked and reaped with one cheap renewal. Reconcile before
            // the cert expires to bank authorised trips; the route resumes once renewed. Surfaced so the hold is
            // visible (Law 4). (8e-2 may split the window at the expiry instant to pay the authorised pre-lapse part.)
            // A scheduled-passenger route (Phase 11f) is gated by the Air Operator Certificate — which gates a route
            // CATEGORY, not a mission type, so RequiredFor(Mission=Passenger) is null. Check it here too, otherwise
            // the AOC would be enforced only at creation and a lapsed cert would keep earning forever (defeating the
            // recurring renewal cost). A plain route keeps its mission-type gate.
            var requiredCert = route.SeatCapacity != null ? CertificateKind.AirOperator : CertificateCatalog.RequiredFor(route.Mission);
            if (requiredCert is { } routeCert && !validCertKinds.Contains(routeCert))
            {
                route.LastReconciledAt = now;
                route.UpdatedAt = now;
                certLapsed.Add($"{route.Name} — {CertificateCatalog.Def(routeCert).DisplayName} lapsed");
                continue;
            }

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
            // Foul weather at the origin scrubs a share of the trips (Phase 8f-2) — same as standing orders.
            var rtOrigin = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == route.OriginIcao, ct);
            int scrubbed = WeatheredTrips(rtOrigin, route.LastReconciledAt, trips, route.RoundTripHours);
            int flown = trips - scrubbed;
            var rtRoll = RollTrips(_cfg, route.Id, route.LastReconciledAt.UtcTicks, flown, rtCrew?.SkillMilli ?? 50_000, route.RewardPerTripCents, route.PriceMultiplierMilli);
            long income = rtRoll.Income;
            if (flown > 0)
            {
                await _ledger.StageBatchAsync(companyId, new[]
                {
                    new LedgerPosting(LedgerCategory.JobPayout, income / 100m, $"Route {route.Name} ×{flown}",
                        AircraftInstanceId: route.AircraftInstanceId, DedupeKey: $"route:{route.Id}:{route.LastReconciledAt.UtcTicks}"),
                }, ct);

                var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == route.AircraftInstanceId, ct);
                if (aircraft is not null)
                {
                    double hours = flown * route.RoundTripHours;
                    aircraft.AirframeHours += hours;
                    int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + rtRoll.ExtraWearMilli;
                    aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                    aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                    aircraft.UpdatedAt = now;
                }
            }
            route.LastReconciledAt = dutyCapped ? now : route.LastReconciledAt.AddHours(trips * route.RoundTripHours);
            route.UpdatedAt = now;
            totalTrips += flown;
            grossIncome += income;
            totalIncidents += rtRoll.Incidents;
            totalEmpty += rtRoll.Empty;
            totalWeatheredOut += scrubbed;
            if (dutyCapped) dutyMaxed.Add(rtAircraft?.Tail ?? route.Name);
            SharpenCrew(rtCrew, flown, now);
            repPullRaw += _cfg.OperatingRepCrewPullMilli(repStartMilli, rtCrew?.SkillMilli ?? 50_000, flown); // Phase 11a
            repFracSum += _cfg.OperatingRepConvergeFrac(flown);
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

        // Aircraft rentals (Phase 9f-1): bill the holding fee + per-flight-hour usage on every active rental,
        // warn before an expiring one auto-returns, and auto-return an expired IDLE rental (Law 4). Billed
        // before the loan block below so the solvency valve sees the cost.
        long totalRental = 0, totalRentalRefund = 0;
        var rentalsExpiring = new List<string>();
        var rentalsReturned = new List<string>();
        foreach (var ag in await _db.RentalAgreements.Where(r => r.CompanyId == companyId && r.Status == RentalStatus.Active && !r.IsDeleted).ToListAsync(ct))
        {
            var tail = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == ag.AircraftInstanceId, ct);
            if (tail is null) // the tail row is gone (an unsupported hard-delete) — close the agreement so it can't bill a ghost forever
            {
                ag.Status = RentalStatus.Returned; ag.UpdatedAt = now;
                continue;
            }
            var rtType = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == tail.TypeId, ct);
            bool expired = now >= ag.ExpiresAt && tail.Availability == AircraftAvailability.Available;

            // Auto-return at expiry — but only an IDLE tail; a rental mid-leg keeps accruing and returns next pass.
            if (expired && rtType is not null)
            {
                var (refund, rent, ins) = await AircraftDealerService.SettleReturnAsync(_db, _ledger, _cfg, ag, tail, rtType, now, ct);
                totalRental += rent; totalInsurance += ins; totalRentalRefund += refund; // fold into the digest so NetCents is exact
                rentalsReturned.Add(tail.Tail);
                continue;
            }
            if (expired) // the type row is gone — can't value the return; close it so billing can't run away (unsupported data state)
            {
                ag.Status = RentalStatus.Returned; ag.UpdatedAt = now;
                rentalsReturned.Add(tail.Tail);
                continue;
            }

            // Bill the period. Keyed on the pre-advance watermark so a replay of the same window dedupes.
            var stamp = ag.LastRentBilledAt.UtcTicks;
            var rp = new List<LedgerPosting>();
            if (ag.Kind == RentalKind.Lease)
            {
                // Lease (Idiom-B): a fixed weekly rate + the lessee-carried hull cover, by whole billed days.
                int days = (int)Math.Floor((now - ag.LastRentBilledAt).TotalDays);
                if (days > 0)
                {
                    long rent = (long)Math.Round(ag.WeeklyRateCents * (days / 7.0));
                    long ins = (long)Math.Round(ag.InsuranceWeeklyCents * (days / 7.0));
                    if (rent > 0)
                        rp.Add(new(LedgerCategory.AircraftRental, -(rent / 100m), $"Lease payment — {tail.Tail}",
                            LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"lease-rent:{ag.Id}:{stamp}"));
                    if (ins > 0)
                        rp.Add(new(LedgerCategory.InsurancePremium, -(ins / 100m), $"Lease hull cover — {tail.Tail}",
                            LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"lease-ins:{ag.Id}:{stamp}"));
                    if (rp.Count > 0) { await _ledger.StageBatchAsync(companyId, rp, ct); totalRental += rent; totalInsurance += ins; }
                    ag.LastRentBilledAt = ag.LastRentBilledAt.AddDays(days); // whole days only; remainder carried
                    ag.RentCreditedCents += rent;                            // rent credits a future buyout
                    ag.UpdatedAt = now;
                }
            }
            else
            {
                // Rental: a holding fee (fractional day, like base rent) + usage (per accrued flight-hour).
                long holding = (long)Math.Round(Math.Max(0, (now - ag.LastRentBilledAt).TotalDays) * ag.HoldingPerDayCents);
                double usageHours = Math.Max(0, tail.AirframeHours - ag.HoursLastBilled);
                long usage = (long)Math.Round(usageHours * ag.FlightHourRateCents);
                if (holding > 0)
                    rp.Add(new(LedgerCategory.AircraftRental, -(holding / 100m), $"Rental holding — {tail.Tail}",
                        LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"rental-hold:{ag.Id}:{stamp}"));
                if (usage > 0)
                    rp.Add(new(LedgerCategory.AircraftRental, -(usage / 100m), $"Rental usage — {tail.Tail} ({usageHours:F1} h)",
                        LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"rental-use:{ag.Id}:{stamp}"));
                if (rp.Count > 0) { await _ledger.StageBatchAsync(companyId, rp, ct); totalRental += holding + usage; }
                ag.LastRentBilledAt = now;
                ag.HoursLastBilled = tail.AirframeHours;
                ag.UpdatedAt = now;
            }

            int warnDays = ag.Kind == RentalKind.Lease ? _cfg.LeaseExpiryWarnDays : _cfg.RentExpiryWarnDays;
            int daysLeft = (int)Math.Ceiling((ag.ExpiresAt - now).TotalDays);
            if (daysLeft > 0 && daysLeft <= warnDays)
                rentalsExpiring.Add($"{tail.Tail} in {daysLeft}d");
        }

        // Loans (Phase 4a) + default safety valve (Phase 7g): billed LAST, so the solvency check sees all of
        // this period's income and other costs. A company that can't cover a payment goes into forbearance
        // (billing pauses so the hole doesn't deepen) and is WARNED; if it stays underwater past the grace
        // window the loan defaults — billing stops for good, the charged-off balance stays on the books (still
        // counted in net worth) and the black mark wrecks its credit. Law 4: warn before the disaster executes.
        // (company was loaded at pass start; its cash reflects the postings staged above — EF identity map.)
        // Oldest debt first, so when cash covers some-but-not-all loans the cascade (which one forbears/defaults)
        // is deterministic and fair, not left to the provider's row order.
        foreach (var loan in await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted).OrderBy(l => l.TakenAt).ToListAsync(ct))
        {
            int days = (int)Math.Floor((now - loan.PaymentLastBilledAt).TotalDays);
            if (days <= 0)
                continue;

            if (company.CashCents < _cfg.LoanDelinquencyCashFloorCents) // can't service the debt this period
            {
                loan.DelinquentSinceAt ??= now; // first miss — start the grace clock
                if ((now - loan.DelinquentSinceAt.Value).TotalDays >= _cfg.LoanDefaultGraceDays)
                {
                    loan.Status = LoanStatus.Defaulted; // grace elapsed while still underwater → written off
                    defaults.Add($"tier {loan.Tier} — {loan.OutstandingCents / 100m:C0} charged off, your credit is wrecked");
                }
                else
                {
                    int left = _cfg.LoanDefaultGraceDays - (int)(now - loan.DelinquentSinceAt.Value).TotalDays;
                    loanWarnings.Add($"tier {loan.Tier} — {left}d to default");
                }
                loan.PaymentLastBilledAt = now; // forbearance: skip this payment, and don't retro-bill the gap later
                loan.UpdatedAt = now;
                continue;
            }

            loan.DelinquentSinceAt = null; // solvent — back in good standing
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

        // Airline operating reputation (Phase 11a): fold this pass's autonomous legs into the company's own name.
        // It eases TOWARD the competence of the crew that flew (L12) — a sharp crew lifts it, a green crew drags
        // it down. Three bounds, in order: (1) a per-pass step cap so a huge backlog can't teleport the name; (2)
        // the trip-weighted crew-skill TARGET the pulls converge to, so summing several lines can never carry the
        // name PAST the operation that actually flew (the L12 upper/lower bound); (3) the hard [0, max] range. A
        // no-elapsed replay flew 0 trips → pull 0 → no move, no event (idempotent). Money-neutral: no ledger row.
        int operatingRepDelta = 0;
        if (repPullRaw != 0)
        {
            // (1) step cap — clamp the summed pull in long space BEFORE narrowing to int, so no order count overflows.
            int repStep = (int)Math.Clamp(repPullRaw,
                                          -(long)_cfg.OperatingRepAutoMaxStepPerPassMilli,
                                           _cfg.OperatingRepAutoMaxStepPerPassMilli);
            int afterRep = repStartMilli + repStep;
            // (2) never overshoot the weighted target: repStart + Σpull/Σfrac is the crew-skill the pulls converge
            // to; clamp the move into [repStart, target] so it can approach but not cross it (repPull and the target
            // share a sign, so this only ever trims an overshoot — it never reverses the move).
            if (repFracSum > 0)
            {
                int target = (int)Math.Round(repStartMilli + repPullRaw / repFracSum);
                afterRep = Math.Clamp(afterRep, Math.Min(repStartMilli, target), Math.Max(repStartMilli, target));
            }
            // (3) hard bounds.
            afterRep = Math.Clamp(afterRep, 0, EconomyConfig.OperatingReputationMax);
            operatingRepDelta = afterRep - repStartMilli;
            if (operatingRepDelta != 0)
            {
                company.OperatingReputationMilli = afterRep;
                _db.AirlineReputationEvents.Add(new AirlineReputationEvent
                {
                    CompanyId = companyId, DeltaMilli = operatingRepDelta, BalanceMilli = afterRep,
                    Source = AirlineRepSource.Crew,
                    Reason = $"Scheduled operations ×{totalTrips} trips", At = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return new ReconcileDigest(totalTrips, grossIncome, totalFees, totalWages, totalRent, totalLoan, totalInsurance,
            grossIncome - totalFees - totalWages - totalRent - totalLoan - totalInsurance - totalRental + totalRentalRefund, totalIncidents, grounded, dutyMaxed, totalEmpty,
            loanWarnings, defaults, certLapsed, totalWeatheredOut, certExpiring, totalRental, rentalsExpiring, rentalsReturned, operatingRepDelta);
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
