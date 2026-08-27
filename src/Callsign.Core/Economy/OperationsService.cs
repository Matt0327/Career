using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A hireable pilot preview. Its wage is economy-set (from a deterministic roll), never player-set.</summary>
public sealed record StaffCandidate(int Seed, string Name, long WagePerDayCents, int SkillMilli);

/// <summary>What a reconcile produced (for the reopen digest).</summary>
public sealed record ReconcileDigest(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long LoanCents, long InsuranceCents, long NetCents, int Incidents, IReadOnlyList<string> Grounded, IReadOnlyList<string> DutyMaxed, int EmptyLegs = 0, IReadOnlyList<string>? LoanWarnings = null, IReadOnlyList<string>? Defaults = null, IReadOnlyList<string>? CertLapsed = null, int WeatheredOut = 0, IReadOnlyList<string>? CertExpiring = null, long RentalCents = 0, IReadOnlyList<string>? RentalsExpiring = null, IReadOnlyList<string>? RentalsAutoReturned = null, int OperatingRepDeltaMilli = 0, long FuelCents = 0, long RepairCents = 0);

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

    /// <summary>A deterministic slate of hireable pilots for a seed, with UNIQUE names, skipping anyone in
    /// <paramref name="exclude"/> (e.g. the pilots already on your roster — so a hired name never lingers in
    /// the market, and the same face never appears twice).</summary>
    public IReadOnlyList<StaffCandidate> GenerateCandidates(int seed, int count = 5, IReadOnlyCollection<string>? exclude = null)
    {
        var rng = new Random(seed);
        var seen = new HashSet<string>(exclude ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var list = new List<StaffCandidate>(count);
        int guard = 0;
        while (list.Count < count && guard++ < count * 40)
        {
            var c = MakeCandidate(rng.Next());
            if (seen.Add(c.Name)) list.Add(c);
        }
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

    /// <summary>Hire the candidate identified by its seed (regenerated server-side, so the wage is trusted). The
    /// new pilot is based at <paramref name="atIcao"/> — the field where you recruit them (Phase 12); null leaves
    /// them un-positioned (they'll be placed when first assigned).</summary>
    public async Task<Staff> HireAsync(Guid companyId, int candidateSeed, string? atIcao = null, CancellationToken ct = default)
    {
        var c = MakeCandidate(candidateSeed);
        var now = _clock.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Name = c.Name, Role = StaffRole.Pilot,
            WagePerDayCents = c.WagePerDayCents, SkillMilli = c.SkillMilli,
            CurrentIcao = string.IsNullOrWhiteSpace(atIcao) ? null : atIcao.Trim().ToUpperInvariant(),
            HiredAt = now, LastPaidAt = now, IsActive = true, UpdatedAt = now,
        };
        _db.Staff.Add(staff);
        await _db.SaveChangesAsync(ct);
        return staff;
    }

    /// <summary>Hire a base MANAGER at one of your fields (Phase 12). A manager auto-services the owned fleet
    /// parked at their field each reconcile, for a flat daily wage. One manager per field; the field must be a
    /// base you run.</summary>
    public async Task<Staff> HireManagerAsync(Guid companyId, string atIcao, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(atIcao)) throw new InvalidOperationException("Choose a base for the manager.");
        var icao = atIcao.Trim().ToUpperInvariant();
        bool hasBase = await _db.Bases.AnyAsync(b => b.CompanyId == companyId && b.AirportIcao == icao && b.IsActive && !b.IsDeleted, ct);
        if (!hasBase) throw new InvalidOperationException($"You need a base at {icao} to station a manager there.");
        bool already = await _db.Staff.AnyAsync(s => s.CompanyId == companyId && s.Role == StaffRole.Manager && s.CurrentIcao == icao && s.IsActive && !s.IsDeleted, ct);
        if (already) throw new InvalidOperationException($"{icao} already has a manager.");
        int seed = icao.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        var now = _clock.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Name = MakeCandidate(seed).Name,
            Role = StaffRole.Manager, WagePerDayCents = _cfg.ManagerWagePerDayCents, SkillMilli = 0,
            CurrentIcao = icao, HiredAt = now, LastPaidAt = now, IsActive = true, UpdatedAt = now,
        };
        _db.Staff.Add(staff);
        await _db.SaveChangesAsync(ct);
        return staff;
    }

    /// <summary>True if a pilot already crews an active standing order, route, or a dispatched leg still in the air
    /// — one crew flies one line (Phase 7f), and a dispatched crew is a line too (Phase 12).</summary>
    public async Task<bool> CrewAlreadyFlyingAsync(Guid staffId, CancellationToken ct = default)
        => await _db.StandingOrders.AnyAsync(o => o.StaffId == staffId && o.IsActive && !o.IsDeleted, ct)
        || await _db.Routes.AnyAsync(r => r.StaffId == staffId && r.Active && !r.IsDeleted, ct)
        || await _db.DispatchLegs.AnyAsync(d => d.StaffId == staffId && d.Status == DispatchStatus.Flying && !d.IsDeleted, ct);

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
        // Phase 12 — the pilot must be WHERE the aircraft is to crew its line. A positioned pilot at another
        // field has to be repositioned first (Crew tab → Reposition). Un-positioned legacy crew (null) grandfather
        // in and are placed at the origin below, so an existing save never breaks.
        if (!string.IsNullOrEmpty(staff.CurrentIcao) && !string.Equals(staff.CurrentIcao, origin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{staff.Name} is at {staff.CurrentIcao}, but the aircraft is at {origin} — reposition the pilot to {origin} first (Crew tab), or pick one already there.");
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
        staff.CurrentIcao = origin; // Phase 12 — the pilot now operates from here (positions a grandfathered crew)
        staff.UpdatedAt = now;
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
    /// Re-price an active route. Reconciles first so trips already flown book at the OLD price (the new price
    /// applies to future trips only). For a plain cargo/charter route this is the markup over the fair rate; for a
    /// scheduled passenger route (Phase 14a) it is the FARE — a real, price-elastic lever (raise the fare to earn
    /// more per seat but thin the cabin, discount it to fill). Returns the clamped price actually stored.
    /// </summary>
    public async Task<int> SetRoutePriceAsync(Guid companyId, Guid routeId, int priceMultiplierMilli, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book pending trips at the current price before it changes
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.CompanyId == companyId && r.Active && !r.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Route not found.");
        int price = route.SeatCapacity != null
            ? ScheduledDemand.ClampFare(_cfg, priceMultiplierMilli)                 // scheduled fare (Phase 14a)
            : Math.Clamp(priceMultiplierMilli, 1000, _cfg.MaxContractMarkupMilli);  // plain-route markup
        route.PriceMultiplierMilli = price;
        route.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return price;
    }

    /// <summary>Cancel a standing order and free its aircraft.</summary>
    public async Task CancelStandingOrderAsync(Guid companyId, Guid orderId, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book any trips flown up to now first
        var order = await _db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order is null || !order.IsActive) return;
        var now = _clock.UtcNow;
        order.IsActive = false;
        order.UpdatedAt = now;
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == order.AircraftInstanceId, ct);
        if (aircraft is not null) { aircraft.Availability = AircraftAvailability.Available; aircraft.UpdatedAt = now; }
        // Phase 12 — the freed pilot is now available at the line's origin, where the aircraft sits and where
        // they were already co-located while flying it. Assigned DIRECTLY (like CancelRouteAsync) — routing
        // through "nearest suitable" could bounce a crew off a heliport/local-code origin to a different field
        // and then the co-location gate would strand them there, unable to re-crew the aircraft or move back.
        var crew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == order.StaffId, ct);
        if (crew is not null) { crew.CurrentIcao = order.OriginIcao; crew.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Dispatch a hired crew + an aircraft to fly a board JOB autonomously as a ONE-WAY leg (Phase 12). The job is
    /// frozen off the board and removed from it; the crew + aircraft must be co-located at the job's origin and the
    /// crew rated for the type; a premium mission needs the company's operating certificate. A RENTAL is allowed
    /// (unlike the perpetual paths) — the leg accrues airframe hours, so the rental's usage fee bills and the leg
    /// is strictly less profitable than in an owned tail. Settled at the next reconcile, which relocates BOTH the
    /// aircraft and the crew to the destination.
    /// </summary>
    public async Task<DispatchLeg> DispatchJobAsync(Guid companyId, Guid jobId, Guid staffId, Guid aircraftId, CancellationToken ct = default)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException("That job is no longer on the board.");
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        // One crew flies one line. A standing order / route blocks dispatch outright; a dispatch itinerary can
        // instead grow to MaxDispatchLegs one-way legs (Phase 12), each departing where the previous one lands.
        if (await _db.StandingOrders.AnyAsync(o => o.StaffId == staffId && o.IsActive && !o.IsDeleted, ct)
            || await _db.Routes.AnyAsync(r => r.StaffId == staffId && r.Active && !r.IsDeleted, ct))
            throw new InvalidOperationException($"{staff.Name} already flies a line — dispatch a different pilot or hire one.");
        var queue = await _db.DispatchLegs.Where(d => d.StaffId == staffId && d.Status == DispatchStatus.Flying && !d.IsDeleted).OrderBy(d => d.ReadyAt).ToListAsync(ct);
        if (queue.Count >= _cfg.MaxDispatchLegs)
            throw new InvalidOperationException($"{staff.Name}'s itinerary is full — a crew flies up to {_cfg.MaxDispatchLegs} legs at a time.");
        var tail = queue.Count > 0 ? queue[^1] : null; // the leg this one continues from, when appending to a run
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftId && a.CompanyId == companyId && !a.IsDeleted, ct)
                       ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (tail is null && aircraft.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("That aircraft isn't available.");
        if (tail is not null && aircraftId != tail.AircraftInstanceId)
            throw new InvalidOperationException($"{staff.Name} is mid-itinerary — the next leg continues in the same aircraft.");
        // A rented tail is fine (its per-flight-hour usage fee keeps a dispatched leg strictly less profitable
        // than an owned one), but a LEASE isn't: a lease bills a flat weekly rate with NO per-hour usage fee, so
        // a leased leg would only TIE an owned one's margin. Fly a lease by hand, or dispatch an owned/rented tail.
        if (aircraft.Ownership != OwnershipKind.Owned
            && await _db.RentalAgreements.AnyAsync(g => g.AircraftInstanceId == aircraft.Id && g.Status == RentalStatus.Active && g.Kind == RentalKind.Lease, ct))
            throw new InvalidOperationException("A leased aircraft can't be dispatched — fly it yourself, or use an owned or rented tail.");

        // The same board-eligibility gates the ACCEPT path enforces (JobAssignmentService), re-checked here so a
        // stale board or a crafted request can't dispatch a crew onto a job the player is locked out of — a job
        // above their rank, or a reputation-gated Emergency/SAR run. Crew skill must never unlock those (L12).
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        if (pilot is not null)
        {
            if (pilot.Rank < job.RequiredRank)
                throw new InvalidOperationException($"{RankTiers.Def(job.RequiredRank).DisplayName} required — a locked job can't be dispatched.");
            var missionDef = MissionCatalog.Def(job.Type);
            if (pilot.ReputationMilli < missionDef.MinReputationMilli)
                throw new InvalidOperationException($"Requires reputation {missionDef.MinReputationMilli / 1000.0:0.0} — a locked job can't be dispatched.");
        }

        // Co-location. A FIRST leg departs where the crew + aircraft physically are (the job's origin). An
        // APPENDED leg must instead depart where the crew's itinerary currently ENDS — the previous leg's
        // destination — since that's where this tail will be by the time it flies. Un-positioned legacy crew
        // (null CurrentIcao) grandfather in and are placed at the origin below.
        if (tail is null)
        {
            if (!string.Equals(aircraft.LocationIcao, job.OriginIcao, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The aircraft is at {aircraft.LocationIcao}, but this job departs {job.OriginIcao} — ferry it there first.");
            if (!string.IsNullOrEmpty(staff.CurrentIcao) && !string.Equals(staff.CurrentIcao, job.OriginIcao, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{staff.Name} is at {staff.CurrentIcao}, but this job departs {job.OriginIcao} — reposition them there first (Crew tab).");
        }
        else if (!string.Equals(tail.DestIcao, job.OriginIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{staff.Name}'s run ends at {tail.DestIcao} — the next leg must depart there.");

        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct);
        int need = _cfg.MinSkillMilliForCategory(type?.Category ?? AircraftCategory.Unknown);
        if (staff.SkillMilli < need)
            throw new InvalidOperationException($"{staff.Name} ({staff.SkillMilli / 1000}%) isn't rated for the {type!.Category} — dispatch a pilot at {need / 1000}%+.");
        if (CertificateCatalog.RequiredFor(job.Type) is { } reqCert
            && !await _db.OperatingCertificates.AnyAsync(c => c.CompanyId == companyId && c.Kind == reqCert && c.ExpiresAt > _clock.UtcNow, ct))
            throw new InvalidOperationException($"{CertificateCatalog.Def(reqCert).DisplayName} required — apply in the Airline tab.");

        double cruise = type?.CruiseKtas ?? 150;
        double oneWayHours = job.DistanceNm / Math.Max(60, cruise);
        var now = _clock.UtcNow;
        // A rented tail earns NO hub-reputation premium — that lift is for your OWN fleet's freelance work (11c/11d).
        long reward = aircraft.Ownership == OwnershipKind.Owned ? job.RewardCents : job.RewardCents - (job.HubRepBonusCents ?? 0);

        var leg = new DispatchLeg
        {
            Id = Guid.NewGuid(), CompanyId = companyId, StaffId = staffId, AircraftInstanceId = aircraftId, JobId = job.Id,
            Type = job.Type, OriginIcao = job.OriginIcao, DestIcao = job.DestIcao, Commodity = job.Commodity ?? "",
            WeightLbs = job.WeightLbs, Pax = job.Pax, DistanceNm = job.DistanceNm, OneWayHours = oneWayHours,
            RewardCents = Math.Max(0, reward), ClientKey = job.ClientKey, ClientName = job.ClientName,
            Status = DispatchStatus.Flying, DispatchedAt = now,
            // Chained off the itinerary: an appended leg starts flying when the previous one lands, so the whole
            // run's duty time accumulates and the FTL cap holds across all legs (not per leg).
            ReadyAt = (tail?.ReadyAt ?? now).AddHours(oneWayHours * 24.0 / Math.Max(1, _cfg.MaxDutyHoursPerDay)),
            UpdatedAt = now,
        };
        _db.DispatchLegs.Add(leg);
        aircraft.Availability = AircraftAvailability.Reserved;
        aircraft.UpdatedAt = now;
        if (tail is null) { staff.CurrentIcao = job.OriginIcao; staff.UpdatedAt = now; } // position a grandfathered crew at the first leg's origin
        _db.Jobs.Remove(job); // committed to the crew — off the board (like accepting it)
        await _db.SaveChangesAsync(ct);
        return leg;
    }

    /// <summary>
    /// Hand a job you've ALREADY ACCEPTED to a hired crew to fly autonomously (Phase 13) — the Fly-tab equivalent
    /// of dispatching a board job. The accepted assignment is marked Abandoned (it leaves your active jobs) and a
    /// one-way <see cref="DispatchLeg"/> is created from its frozen snapshot. First-leg only: if the crew already
    /// flies a line, hand off from the board or pick a free pilot. Same crew/aircraft/co-location gates as a
    /// board dispatch, so a hand-off can never bypass what a direct dispatch enforces.
    /// </summary>
    public async Task<DispatchLeg> DispatchAssignmentAsync(Guid companyId, Guid assignmentId, Guid staffId, Guid aircraftId, CancellationToken ct = default)
    {
        var a = await _db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.AccountId == companyId, ct)
                ?? throw new InvalidOperationException("Job not found.");
        if (a.Status != AssignmentStatus.Accepted)
            throw new InvalidOperationException("That job can no longer be handed to a crew.");
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        if (await _db.StandingOrders.AnyAsync(o => o.StaffId == staffId && o.IsActive && !o.IsDeleted, ct)
            || await _db.Routes.AnyAsync(r => r.StaffId == staffId && r.Active && !r.IsDeleted, ct))
            throw new InvalidOperationException($"{staff.Name} already flies a line — pick a different pilot.");
        if (await _db.DispatchLegs.AnyAsync(d => d.StaffId == staffId && d.Status == DispatchStatus.Flying && !d.IsDeleted, ct))
            throw new InvalidOperationException($"{staff.Name} is already on a run — hand this off from the Jobs board to chain it, or pick a free pilot.");

        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(x => x.Id == aircraftId && x.CompanyId == companyId && !x.IsDeleted, ct)
                       ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (aircraft.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("That aircraft isn't available.");
        if (aircraft.Ownership != OwnershipKind.Owned
            && await _db.RentalAgreements.AnyAsync(g => g.AircraftInstanceId == aircraft.Id && g.Status == RentalStatus.Active && g.Kind == RentalKind.Lease, ct))
            throw new InvalidOperationException("A leased aircraft can't be dispatched — fly it yourself, or use an owned or rented tail.");
        if (!string.Equals(aircraft.LocationIcao, a.OriginIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The aircraft is at {aircraft.LocationIcao}, but this job departs {a.OriginIcao} — ferry it there first.");
        if (!string.IsNullOrEmpty(staff.CurrentIcao) && !string.Equals(staff.CurrentIcao, a.OriginIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{staff.Name} is at {staff.CurrentIcao}, but this job departs {a.OriginIcao} — reposition them there first (Crew tab).");

        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct);
        int need = _cfg.MinSkillMilliForCategory(type?.Category ?? AircraftCategory.Unknown);
        if (staff.SkillMilli < need)
            throw new InvalidOperationException($"{staff.Name} ({staff.SkillMilli / 1000}%) isn't rated for the {type!.Category} — pick a pilot at {need / 1000}%+.");
        if (CertificateCatalog.RequiredFor(a.Type) is { } reqCert
            && !await _db.OperatingCertificates.AnyAsync(c => c.CompanyId == companyId && c.Kind == reqCert && c.ExpiresAt > _clock.UtcNow, ct))
            throw new InvalidOperationException($"{CertificateCatalog.Def(reqCert).DisplayName} required — apply in the Airline tab.");

        double cruise = type?.CruiseKtas ?? 150;
        double oneWayHours = a.DistanceNm / Math.Max(60, cruise);
        var now = _clock.UtcNow;
        var leg = new DispatchLeg
        {
            Id = Guid.NewGuid(), CompanyId = companyId, StaffId = staffId, AircraftInstanceId = aircraftId, JobId = a.JobId,
            Type = a.Type, OriginIcao = a.OriginIcao, DestIcao = a.DestIcao, Commodity = a.Commodity ?? "",
            WeightLbs = a.WeightLbs, Pax = a.Pax, DistanceNm = a.DistanceNm, OneWayHours = oneWayHours,
            RewardCents = Math.Max(0, a.RewardQuoteCents), ClientKey = a.ClientKey, ClientName = a.ClientName,
            Status = DispatchStatus.Flying, DispatchedAt = now,
            ReadyAt = now.AddHours(oneWayHours * 24.0 / Math.Max(1, _cfg.MaxDutyHoursPerDay)),
            UpdatedAt = now,
        };
        _db.DispatchLegs.Add(leg);
        aircraft.Availability = AircraftAvailability.Reserved;
        aircraft.UpdatedAt = now;
        staff.CurrentIcao = a.OriginIcao; staff.UpdatedAt = now;
        a.Status = AssignmentStatus.Abandoned; // handed to the crew — the leg now carries it
        await _db.SaveChangesAsync(ct);
        return leg;
    }

    /// <summary>Recall a dispatched leg still in the air — and every LATER leg of that crew's itinerary, since a
    /// downstream leg departs where an upstream one lands, so cancelling one orphans the rest. Those jobs are
    /// forfeited (they already left the board). The tail is freed only if no EARLIER leg still holds it. Reconcile-
    /// first banks a leg that just landed. Idempotent — a no-op if the leg already completed or was recalled.</summary>
    public async Task CancelDispatchAsync(Guid companyId, Guid legId, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // bank it if it just completed
        var leg = await _db.DispatchLegs.FirstOrDefaultAsync(d => d.Id == legId && d.CompanyId == companyId, ct);
        if (leg is null || leg.Status != DispatchStatus.Flying || leg.IsDeleted) return;
        var now = _clock.UtcNow;
        // This leg and everything downstream of it in the same crew's itinerary.
        var toCancel = await _db.DispatchLegs.Where(d => d.CompanyId == companyId && d.StaffId == leg.StaffId
            && d.Status == DispatchStatus.Flying && !d.IsDeleted && d.ReadyAt >= leg.ReadyAt).ToListAsync(ct);
        foreach (var c in toCancel) { c.IsDeleted = true; c.UpdatedAt = now; }
        // Free the tail only if no EARLIER leg of this run is still flying it (those weren't cancelled).
        bool earlierLegHolds = await _db.DispatchLegs.AnyAsync(d => d.CompanyId == companyId && d.AircraftInstanceId == leg.AircraftInstanceId
            && d.Status == DispatchStatus.Flying && !d.IsDeleted && d.ReadyAt < leg.ReadyAt, ct);
        if (!earlierLegHolds)
        {
            var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == leg.AircraftInstanceId, ct);
            if (aircraft is not null && aircraft.Availability == AircraftAvailability.Reserved) { aircraft.Availability = AircraftAvailability.Available; aircraft.UpdatedAt = now; }
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reposition (deadhead) a hired pilot to another field for a fee (Phase 12) — the crew equivalent of ferrying
    /// an aircraft. Much cheaper (one commercial seat, no fleet fuel/wear). The pilot can't be mid-line; the target
    /// must be a real, landable airport. Returns the fee paid (0 on a no-op replay). Idempotent per dedupe key.
    /// </summary>
    public async Task<long> RelocateCrewAsync(Guid companyId, Guid staffId, string destIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"crewpos:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return -prior.AmountCents;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException("Company not found.");
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        if (await CrewAlreadyFlyingAsync(staffId, ct))
            throw new InvalidOperationException($"{staff.Name} is flying a line — cancel it before repositioning them.");

        destIcao = (destIcao ?? "").Trim().ToUpperInvariant();
        if (destIcao.Length == 0) throw new InvalidOperationException("Pick a destination.");
        if (string.Equals(destIcao, staff.CurrentIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{staff.Name} is already at {destIcao}.");

        var to = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                 ?? throw new InvalidOperationException($"Unknown airport {destIcao}.");
        if (!AirportSuitability.IsSuitable(to)) // crew deadhead in on a commercial seat: needs a real, landable field (no runway requirement)
            throw new InvalidOperationException($"{destIcao} isn't a field crew can position to.");
        var from = string.IsNullOrEmpty(staff.CurrentIcao) ? null
            : await _db.Airports.FirstOrDefaultAsync(a => a.Ident == staff.CurrentIcao, ct);
        double distanceNm = from is null ? 0 : GeoMath.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        long fee = _cfg.CrewPositionBaseCents + (long)Math.Round(distanceNm * _cfg.CrewPositionPerNmCents);
        if (company.CashCents < fee)
            throw new InvalidOperationException($"Not enough cash: repositioning costs {fee / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.CrewPositioning, -(fee / 100m),
                $"Reposition {staff.Name}: {staff.CurrentIcao ?? "—"} → {destIcao} ({distanceNm:F0} nm)",
                StaffId: staff.Id, DedupeKey: dedupe ?? $"crewpos:{staff.Id}:{destIcao}:{staff.UpdatedAt.UtcTicks}"),
        }, ct);
        staff.CurrentIcao = destIcao;
        staff.UpdatedAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return -raced.AmountCents;
            throw;
        }
        return fee;
    }

    /// <summary>
    /// Reposition the PLAYER (you) to another field for a fee (Phase 13) — the inverse of ferrying an aircraft to
    /// you: sometimes it's cheaper to travel to where the aircraft (or a job's departure field) already is. Same
    /// commercial-seat deadhead economics as a crew reposition. The target must be a real, landable airport.
    /// Returns the fee paid (0 on a no-op replay). Idempotent per dedupe key.
    /// </summary>
    public async Task<long> RelocatePlayerAsync(Guid companyId, string destIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"playerpos:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return -prior.AmountCents;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException("Company not found.");
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");

        destIcao = (destIcao ?? "").Trim().ToUpperInvariant();
        if (destIcao.Length == 0) throw new InvalidOperationException("Pick a destination.");
        if (string.Equals(destIcao, pilot.CurrentIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"You're already at {destIcao}.");

        var to = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                 ?? throw new InvalidOperationException($"Unknown airport {destIcao}.");
        if (!AirportSuitability.IsSuitable(to))
            throw new InvalidOperationException($"{destIcao} isn't a field you can travel to.");
        var from = string.IsNullOrEmpty(pilot.CurrentIcao) ? null
            : await _db.Airports.FirstOrDefaultAsync(a => a.Ident == pilot.CurrentIcao, ct);
        double distanceNm = from is null ? 0 : GeoMath.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        long fee = _cfg.CrewPositionBaseCents + (long)Math.Round(distanceNm * _cfg.CrewPositionPerNmCents);
        if (company.CashCents < fee)
            throw new InvalidOperationException($"Not enough cash: the trip costs {fee / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.CrewPositioning, -(fee / 100m),
                $"Travel {pilot.CurrentIcao ?? "—"} → {destIcao} ({distanceNm:F0} nm)",
                DedupeKey: dedupe ?? $"playerpos:{destIcao}:{pilot.UpdatedAt.UtcTicks}"),
        }, ct);
        pilot.CurrentIcao = destIcao;
        pilot.UpdatedAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return -raced.AmountCents;
            throw;
        }
        return fee;
    }

    /// <summary>
    /// Let a hired pilot go (Phase 12): a soft dismiss (IsActive=false) that stops their wage accruing. They
    /// can't be dismissed mid-line — cancel the line first. Idempotent (a no-op if already gone).
    /// </summary>
    public async Task DismissAsync(Guid companyId, Guid staffId, CancellationToken ct = default)
    {
        // Book the wage owed up to now before the pilot leaves the payroll: once IsActive flips false the
        // reconcile wage loop skips them forever, so without this the [LastPaidAt, now] segment would be lost.
        // Mirrors CancelStandingOrderAsync, which reconciles-first for the same reason.
        await ReconcileAsync(companyId, ct);
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted, ct);
        if (staff is null) return;
        if (await CrewAlreadyFlyingAsync(staffId, ct))
            throw new InvalidOperationException($"{staff.Name} is flying a line — cancel it before letting them go.");
        staff.IsActive = false;
        staff.UpdatedAt = _clock.UtcNow;
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
        long grossIncome = 0, totalFees = 0, totalWages = 0, totalRent = 0, totalLoan = 0, totalInsurance = 0, totalFuel = 0, totalRepair = 0;
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

        // Phase 16c — the executive suite. A strong org runs the autonomous operation better: its management
        // strength adds to every crew's EFFECTIVE skill for the operating-rep pull below, so ops converge toward a
        // higher name. Read once. 0 when there's no C-suite → the pulls are byte-identical to pre-16c. The pull is
        // still step-capped, never overshoots its target, and is hard-bounded to 100 — so this only ever helps,
        // within those rails. Executives are also paid a daily salary in the wage loop further down.
        var execs = await _db.Executives.Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted).ToListAsync(ct);
        // Phase 16f ratchet-only rail: if you can't afford the C-suite (cash below the same floor loans forbear at),
        // they're FURLOUGHED this pass — unpaid (skipped in the salary loop below) and their effects don't apply, so
        // an over-large org STALLS the operation instead of bleeding you toward ruin. Warned; fully recoverable once
        // cash recovers (L9 / Law 4 — never a silent wipe-out). `effExecs` is the org that's actually working.
        bool execsFurloughed = execs.Count > 0 && company.CashCents < _cfg.LoanDelinquencyCashFloorCents;
        var effExecs = execsFurloughed ? new List<Callsign.Core.Domain.Executive>() : execs;
        int orgSkillBoostMilli = ExecutiveService.OrgSkillBoostMilli(_cfg, ExecutiveService.OrgStrengthMilli(effExecs, _cfg.ExecutiveRoleCount));
        int Managed(int crewSkillMilli) => Math.Min(100_000, crewSkillMilli + orgSkillBoostMilli);

        // Phase 16d — crew fatigue. A crew's EFFECTIVE flying skill drops with fatigue (worse trip rolls + a
        // slower-growing name); duty on a line accrues it, the window's rest sheds it. A Chief Pilot's rostering
        // (a 16c executive) eases both ends. relief 0 when there's no Chief Pilot → fatigue is unmitigated. All
        // start at 0 fatigue, so the FIRST pass of any line is byte-identical to pre-16d; it accrues afterward.
        var chiefPilot = effExecs.FirstOrDefault(e => e.Role == Callsign.Core.Domain.ExecutiveRole.ChiefPilot);
        double fatigueRelief = chiefPilot is null ? 0.0 : Math.Clamp(chiefPilot.CompetenceMilli / 100_000.0 * _cfg.ChiefPilotFatigueReliefFactor, 0.0, 0.95);

        // Phase 16d specialists — each executive's own bounded lever on the autonomous operation (0 with no holder,
        // so byte-identical to before when the seat's empty). They interact: the COO's extra throughput burns more
        // crew fatigue, fuel and airframe unless the Chief Pilot / CFO / Maintenance Director offset it.
        double CompOf(Callsign.Core.Domain.ExecutiveRole role) => (effExecs.FirstOrDefault(e => e.Role == role)?.CompetenceMilli ?? 0) / 100_000.0;
        double cooDutyMult = 1.0 + CompOf(Callsign.Core.Domain.ExecutiveRole.ChiefOperating) * _cfg.CooDutyBonusFactor;
        double cfoFuelMult = 1.0 - CompOf(Callsign.Core.Domain.ExecutiveRole.ChiefFinancial) * _cfg.CfoFuelDiscountFactor;
        double maintWearMult = 1.0 - CompOf(Callsign.Core.Domain.ExecutiveRole.MaintenanceDirector) * _cfg.MaintWearReductionFactor;
        int DutyCap(int baseTrips) => (int)Math.Floor(baseTrips * cooDutyMult);
        long Fueled(long fuelCents) => (long)Math.Round(fuelCents * cfoFuelMult);
        int Worn(int wearMilli) => (int)Math.Round(wearMilli * maintWearMult);
        // Phase 16e — the Network Planner defends your share: it scales DOWN the rival-pressure your dominance
        // provokes on scheduled routes (below). 0 with no holder → rivals mobilise unchecked.
        double npDefense = CompOf(Callsign.Core.Domain.ExecutiveRole.NetworkPlanner) * _cfg.NetworkPlannerCompetitionDefenseFactor;
        int FlySkill(Staff? crew)
        {
            int penalty = (int)Math.Round((crew?.FatigueMilli ?? 0) / 100_000.0 * _cfg.CrewFatigueSkillPenaltyMax);
            return Math.Clamp((crew?.SkillMilli ?? 50_000) - penalty, 0, 100_000);
        }
        void AccrueFatigue(Staff? crew, double dutyHours, double restHours)
        {
            if (crew is null) return;
            double gain = dutyHours * _cfg.CrewFatiguePerDutyHourMilli * (1 - fatigueRelief);
            double recover = restHours * _cfg.CrewFatigueRecoveryPerRestHourMilli * (1 + fatigueRelief);
            crew.FatigueMilli = (int)Math.Clamp(crew.FatigueMilli + gain - recover, 0, 100_000);
            crew.UpdatedAt = now;
        }

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
            int soMaxTrips = DutyCap(_cfg.AutonomousTripsFlown(elapsedH, o.RoundTripHours)); // duty cap (+COO throughput, 16d)
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
            var soRoll = RollTrips(_cfg, o.Id, o.LastReconciledAt.UtcTicks, flown, FlySkill(soCrew), o.RewardPerTripCents, o.PriceMultiplierMilli); // FlySkill = fatigue-adjusted (16d)
            long income = soRoll.Income;
            long fees = flown * feePerTrip;
            var stamp = o.LastReconciledAt.UtcTicks;

            if (flown > 0)
            {
                double hours = flown * o.RoundTripHours;
                var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == o.AircraftInstanceId, ct);
                var soType = aircraft is not null ? await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct) : null;
                long fuel = Fueled(aircraft is not null ? _cfg.EstimatedFuelCents(soType?.Category ?? AircraftCategory.Unknown, hours) : 0); // Wave-2: autonomous legs pay fuel; Fueled = −CFO (16d)

                var postings = new List<LedgerPosting>
                {
                    new(LedgerCategory.JobPayout, income / 100m, $"Standing order {o.OriginIcao}↔{o.DestIcao} ×{flown}",
                        AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}"),
                };
                if (fees > 0)
                    postings.Add(new(LedgerCategory.AirportFee, -(fees / 100m), $"Landing fees {o.OriginIcao}↔{o.DestIcao} ×{flown}",
                        AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}:fee"));
                if (fuel > 0)
                    postings.Add(new(LedgerCategory.Fuel, -(fuel / 100m), $"Fuel — standing order {o.OriginIcao}↔{o.DestIcao} ×{flown}",
                        AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}:fuel"));
                await _ledger.StageBatchAsync(companyId, postings, ct);

                if (aircraft is not null)
                {
                    aircraft.AirframeHours += hours;
                    int wear = Worn((int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + soRoll.ExtraWearMilli); // Worn = −Maintenance Director (16d)
                    aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                    aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                    aircraft.UpdatedAt = now;
                }
                totalFuel += fuel; // Wave-2 — tracked distinctly from landing fees
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
            int soCrewSkillFlown = FlySkill(soCrew); // fatigue-adjusted competence that FLEW (16d), captured BEFORE SharpenCrew/AccrueFatigue — the L12 target
            SharpenCrew(soCrew, flown, now);
            AccrueFatigue(soCrew, flown * o.RoundTripHours, Math.Max(0, elapsedH - flown * o.RoundTripHours)); // 16d — duty tires, the window's rest recovers
            repPullRaw += _cfg.OperatingRepCrewPullMilli(repStartMilli, Managed(soCrewSkillFlown), flown); // Phase 11a; Managed = +org (16c)
            repFracSum += _cfg.OperatingRepConvergeFrac(flown);
        }

        // Phase 12 — dispatched one-off crew JOBS (one-way legs). A leg completes once its duty-scaled flight time
        // has elapsed (ReadyAt), then it books ONCE (dedupe dispatch:{id}) through the same machinery as the round-
        // trip lines: RollTrips decides the outcome from crew skill, landing fees bill (waived at your bases), the
        // airframe accrues its hours + wear — CRUCIALLY before the rental billing loop below, so a rented tail's
        // usage fee bills these hours — and BOTH the aircraft and the crew relocate ONE-WAY to the destination.
        // The company's currently-valid operating certificates — so a premium dispatched leg (VIP/hazmat) held
        // past its ReadyAt while the cert expires can't settle on a lapsed cert. HELD, not forfeited (it was
        // authorised when dispatched, and the airworthiness pattern above holds rather than forfeits) — L9.
        var validDispatchCerts = (await _db.OperatingCertificates.Where(c => c.CompanyId == companyId && c.ExpiresAt > now).Select(c => c.Kind).ToListAsync(ct)).ToHashSet();
        // Ordered by ReadyAt so a multi-leg itinerary settles IN SEQUENCE — leg A→B (moving the tail to B) before
        // B→C, which departs B. The chained ReadyAt guarantees the order matches the physical route.
        var dispatchLegs = await _db.DispatchLegs.Where(d => d.CompanyId == companyId && d.Status == DispatchStatus.Flying && !d.IsDeleted).OrderBy(d => d.ReadyAt).ToListAsync(ct);
        foreach (var leg in dispatchLegs)
        {
            if (leg.ReadyAt > now)
                continue; // still en route — the duty-scaled flight time hasn't elapsed yet
            var legAircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == leg.AircraftInstanceId, ct);
            if (legAircraft is not null && AircraftDealerService.AirworthinessOf(legAircraft, _cfg, now) is { Airworthy: false } legAw)
            {
                grounded.Add($"{legAircraft.Tail} — {legAw.Reason}"); // hold the leg until the tail is serviced
                continue;
            }
            if (CertificateCatalog.RequiredFor(leg.Type) is { } legCert && !validDispatchCerts.Contains(legCert))
            {
                grounded.Add($"{legAircraft?.Tail ?? leg.OriginIcao} — {CertificateCatalog.Def(legCert).DisplayName} lapsed, renew to fly");
                continue; // hold the leg until the certificate is renewed
            }
            var legCrew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == leg.StaffId, ct);
            int legSkill = FlySkill(legCrew); // 16d — a fatigued crew flies the dispatch worse too (accrual stays a repeating-line mechanic: a one-off has no rest window)
            // One leg at the fair rate (markup 1000 → pFill 1.0: an accepted job never flies empty). Crew skill
            // still decides clean / minor scuff / diversion (half pay) / lost trip, seeded off the leg id.
            // Phase 13 — a hired pilot earns less than you flying it yourself (you skipped the seat).
            long crewReward = (long)Math.Round(leg.RewardCents * _cfg.CrewLegPayFactor);
            var roll = RollTrips(_cfg, leg.Id, leg.DispatchedAt.UtcTicks, 1, legSkill, crewReward);
            var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == leg.OriginIcao, ct);
            var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == leg.DestIcao, ct);
            long fees = (oAir is not null && !baseIcaos.Contains(leg.OriginIcao) ? _cfg.LandingFeeCents(oAir.Kind) : 0)
                      + (dAir is not null && !baseIcaos.Contains(leg.DestIcao) ? _cfg.LandingFeeCents(dAir.Kind) : 0);
            var legType = legAircraft is not null ? await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == legAircraft.TypeId, ct) : null;
            long fuel = Fueled(legAircraft is not null ? _cfg.EstimatedFuelCents(legType?.Category ?? AircraftCategory.Unknown, leg.OneWayHours) : 0); // Wave-2: dispatched legs pay fuel; Fueled = −CFO (16d)
            var postings = new List<LedgerPosting>
            {
                new(LedgerCategory.JobPayout, roll.Income / 100m, $"Dispatch {leg.OriginIcao}→{leg.DestIcao} ({leg.ClientName ?? leg.Commodity})",
                    AircraftInstanceId: leg.AircraftInstanceId, StaffId: leg.StaffId, DedupeKey: $"dispatch:{leg.Id}"),
            };
            if (fees > 0)
                postings.Add(new(LedgerCategory.AirportFee, -(fees / 100m), $"Landing fees {leg.OriginIcao}→{leg.DestIcao}",
                    AircraftInstanceId: leg.AircraftInstanceId, DedupeKey: $"dispatch:{leg.Id}:fee"));
            if (fuel > 0)
                postings.Add(new(LedgerCategory.Fuel, -(fuel / 100m), $"Fuel — dispatch {leg.OriginIcao}→{leg.DestIcao}",
                    AircraftInstanceId: leg.AircraftInstanceId, DedupeKey: $"dispatch:{leg.Id}:fuel"));
            await _ledger.StageBatchAsync(companyId, postings, ct);

            if (legAircraft is not null)
            {
                legAircraft.AirframeHours += leg.OneWayHours;
                int wear = Worn((int)Math.Round(leg.OneWayHours * _cfg.ConditionWearMilliPerHour) + roll.ExtraWearMilli); // Worn = −Maintenance Director (16d)
                legAircraft.HullConditionMilli = Math.Max(0, legAircraft.HullConditionMilli - wear);
                legAircraft.EngineConditionMilli = Math.Max(0, legAircraft.EngineConditionMilli - wear);
                legAircraft.LocationIcao = leg.DestIcao;                       // one-way: the tail is now at the destination
                legAircraft.UpdatedAt = now;                                   // freed below — only once its whole itinerary is flown
            }
            if (legCrew is not null) { legCrew.CurrentIcao = leg.DestIcao; legCrew.UpdatedAt = now; } // the crew flew there too

            // Phase 13 — the client hears about a hired-pilot delivery too, just with a smaller lift than if YOU
            // flew it. Only on a delivery that actually earned (a lost trip pleases no one). Starts the relationship
            // if this is a new client. (Autonomous legs used to update no client at all.)
            if (leg.ClientKey is { } legClientKey && roll.Income > 0)
            {
                var client = await _db.Clients.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.ClientKey == legClientKey, ct);
                if (client is null)
                {
                    client = new Client
                    {
                        Id = Guid.NewGuid(), CompanyId = companyId, ClientKey = legClientKey,
                        Name = leg.ClientName ?? legClientKey, HomeIcao = leg.OriginIcao,
                        LoyaltyMilli = 0, FirstSeenAt = now, UpdatedAt = now,
                    };
                    _db.Clients.Add(client);
                }
                client.LoyaltyMilli = Math.Clamp(client.LoyaltyMilli + _cfg.CrewLegClientLoyaltyMilli, 0, EconomyConfig.LoyaltyMax);
                client.LastJobAt = now;
                client.UpdatedAt = now;
            }
            SharpenCrew(legCrew, 1, now);
            repPullRaw += _cfg.OperatingRepCrewPullMilli(repStartMilli, Managed(legSkill), 1); // legSkill captured BEFORE SharpenCrew; Managed = +org (16c)
            repFracSum += _cfg.OperatingRepConvergeFrac(1);

            leg.Status = DispatchStatus.Flown;
            leg.FlownAt = now;
            leg.UpdatedAt = now;
            totalTrips += 1;
            grossIncome += roll.Income;
            totalFees += fees;
            totalFuel += fuel; // Wave-2 — tracked distinctly from landing fees
            totalIncidents += roll.Incidents;
        }
        // Free each dispatched tail only once its WHOLE itinerary is flown — a tail mid-run (a later leg still
        // Flying, or a leg held for weather/airworthiness/cert) stays Reserved so it can't be double-booked.
        foreach (var tailId in dispatchLegs.Select(l => l.AircraftInstanceId).Distinct())
        {
            if (dispatchLegs.Any(l => l.AircraftInstanceId == tailId && l.Status == DispatchStatus.Flying))
                continue;
            var freed = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == tailId, ct);
            if (freed is not null && freed.Availability == AircraftAvailability.Reserved) { freed.Availability = AircraftAvailability.Available; freed.UpdatedAt = now; }
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

        // Phase 16c — executive salaries accrue like wages, on the same ledger path (the real cost of the org).
        // Phase 16f — but a C-suite you can't afford is FURLOUGHED (unpaid, effects already off above), so the org
        // stalls instead of bleeding you: their LastPaidAt still advances so the unpaid gap is never retro-billed
        // when cash recovers (same as loan forbearance). Warned in the digest; fully recoverable.
        if (execsFurloughed)
        {
            long unpaidPerDay = execs.Sum(e => e.SalaryPerDayCents);
            foreach (var e in execs) { e.LastPaidAt = now; e.UpdatedAt = now; }
            loanWarnings.Add($"C-suite furloughed — {unpaidPerDay / 100m:C0}/day unpaid, their effects paused until cash recovers");
        }
        else foreach (var e in execs)
        {
            double execDays = (now - e.LastPaidAt).TotalDays;
            long salary = (long)Math.Round(execDays * e.SalaryPerDayCents);
            if (salary <= 0)
                continue;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.StaffWage, -(salary / 100m), $"Salary — {e.Name}",
                    DedupeKey: $"execwage:{e.Id}:{e.LastPaidAt.UtcTicks}"),
            }, ct);
            e.LastPaidAt = now;
            e.UpdatedAt = now;
            totalWages += salary;
        }

        // Base managers (Phase 12): each manager keeps the OWNED fleet parked AT THEIR FIELD airworthy —
        // auto-servicing any tail that's come due, so an autonomous operation isn't grounded while you're away.
        // Billed like a manual service (same dedupe key, so a manual service this window isn't double-charged),
        // staged into this one reconcile transaction. Condition + watermark are reset exactly as MaintainAsync does.
        var managers = await _db.Staff.Where(s => s.CompanyId == companyId && s.Role == StaffRole.Manager
            && s.IsActive && !s.IsDeleted && s.CurrentIcao != null).ToListAsync(ct);
        foreach (var mgr in managers)
        {
            var tails = await _db.AircraftInstances.Where(a => a.CompanyId == companyId && !a.IsDeleted
                && a.Ownership == OwnershipKind.Owned && a.LocationIcao == mgr.CurrentIcao
                && a.Availability == AircraftAvailability.Available).ToListAsync(ct);
            foreach (var t in tails)
            {
                if (t.AirframeHours - t.MaintenanceHoursWatermark < _cfg.MaintenanceIntervalHours) continue; // not due yet
                long cost = _cfg.MaintenanceBaseCents
                    + (long)Math.Round(Math.Max(0, t.AirframeHours - t.MaintenanceHoursWatermark) * _cfg.MaintenancePerHourCents);
                await _ledger.StageBatchAsync(companyId, new[]
                {
                    new LedgerPosting(LedgerCategory.Repair, -(cost / 100m), $"Manager service on {t.Tail} at {mgr.CurrentIcao}",
                        AircraftInstanceId: t.Id, DedupeKey: $"maint:{t.Id}:{t.AirframeHours:F1}"),
                }, ct);
                t.HullConditionMilli = 100_000;
                t.EngineConditionMilli = 100_000;
                t.MaintenanceHoursWatermark = t.AirframeHours;
                t.UpdatedAt = now;
                totalRepair += cost;
            }
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

            int rtMaxTrips = DutyCap(_cfg.AutonomousTripsFlown(elapsedH, route.RoundTripHours)); // duty cap (+COO throughput, 16d)
            int trips = Math.Min(rawTrips, rtMaxTrips);
            bool dutyCapped = trips < rawTrips;
            if (trips <= 0)
                continue; // duty not yet accrued — the crew rests

            var rtCrew = await _db.Staff.FirstOrDefaultAsync(s => s.Id == route.StaffId, ct);
            // Foul weather at the origin scrubs a share of the trips (Phase 8f-2) — same as standing orders.
            var rtOrigin = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == route.OriginIcao, ct);
            int scrubbed = WeatheredTrips(rtOrigin, route.LastReconciledAt, trips, route.RoundTripHours);
            int flown = trips - scrubbed;
            // Phase 14a — living demand: a scheduled passenger route no longer earns its frozen per-trip rate.
            // Each pass the cabin fill is recomputed from the airline's CURRENT operating reputation, the season,
            // and the fare the player set, and the per-trip revenue follows. The fare's demand effect lives in the
            // load factor, so the markup pFill is neutralised (a scheduled seat never "declines the job"). A plain
            // cargo/charter route (SeatCapacity null) is unchanged — its frozen rate and markup ride as before (L10).
            long perTripReward = route.RewardPerTripCents;
            int rollMarkup = route.PriceMultiplierMilli;
            if (route.SeatCapacity is int rtSeats && route.SeatYieldCents is long rtYield)
            {
                double season = ScheduledDemand.SeasonMultiplier(_cfg, now);
                // Phase 14b — your share of the route's market (vs its rivals) flexes the cabin fill; Phase 14c — a
                // poor on-time record dampens demand too (passengers avoid an unreliable line). Both bounded.
                var comp = RouteCompetition.Evaluate(_cfg, route.Id, repStartMilli, route.PriceMultiplierMilli, route.RivalPressureMilli);
                double marketMult = comp.LoadMultiplier * _cfg.ReliabilityLoadMultiplier(route.ReliabilityMilli);
                // Phase 16e — the rivals react: pressure eases toward the target your underlying dominance provokes
                // (a Network Planner scales that target down). Dominate a line and the war escalates over passes;
                // retreat and it cools. Applies next pass, so this pass's fill used the pressure as it stood.
                int pressureTarget = RouteCompetition.PressureTarget(_cfg, route.Id, comp.RawShareMilli, npDefense);
                route.RivalPressureMilli = Math.Clamp(
                    (int)Math.Round(route.RivalPressureMilli + _cfg.CompetitionPressureEmaAlpha * (pressureTarget - route.RivalPressureMilli)),
                    0, 100_000);
                int liveLoad = ScheduledDemand.LoadFactorMilli(_cfg, repStartMilli, season, route.PriceMultiplierMilli, marketMult);
                perTripReward = ScheduledDemand.RevenuePerTripCents(rtSeats, liveLoad, rtYield, route.PriceMultiplierMilli);
                rollMarkup = 1000;
            }
            var rtRoll = RollTrips(_cfg, route.Id, route.LastReconciledAt.UtcTicks, flown, FlySkill(rtCrew), perTripReward, rollMarkup); // FlySkill = fatigue-adjusted (16d)
            long income = rtRoll.Income;
            if (flown > 0)
            {
                double hours = flown * route.RoundTripHours;
                var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == route.AircraftInstanceId, ct);
                var rtType = aircraft is not null ? await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct) : null;
                long fuel = Fueled(aircraft is not null ? _cfg.EstimatedFuelCents(rtType?.Category ?? AircraftCategory.Unknown, hours) : 0); // Wave-2: routes pay fuel; Fueled = −CFO (16d)

                var postings = new List<LedgerPosting>
                {
                    new(LedgerCategory.JobPayout, income / 100m, $"Route {route.Name} ×{flown}",
                        AircraftInstanceId: route.AircraftInstanceId, DedupeKey: $"route:{route.Id}:{route.LastReconciledAt.UtcTicks}"),
                };
                if (fuel > 0)
                    postings.Add(new(LedgerCategory.Fuel, -(fuel / 100m), $"Fuel — route {route.Name} ×{flown}",
                        AircraftInstanceId: route.AircraftInstanceId, DedupeKey: $"route:{route.Id}:{route.LastReconciledAt.UtcTicks}:fuel"));
                await _ledger.StageBatchAsync(companyId, postings, ct);

                if (aircraft is not null)
                {
                    aircraft.AirframeHours += hours;
                    int wear = Worn((int)Math.Round(hours * _cfg.ConditionWearMilliPerHour) + rtRoll.ExtraWearMilli); // Worn = −Maintenance Director (16d)
                    aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                    aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                    aircraft.UpdatedAt = now;
                }
                totalFuel += fuel; // routes are fee-free at both bases; fuel is their operating cost
            }
            // Phase 14c — update the scheduled route's rolling on-time record from THIS pass: the share of the
            // scheduled trips flown clean (weather cancellations and crew diversions both drag it down), blended in.
            if (route.SeatCapacity != null && trips > 0)
            {
                int cleanTrips = Math.Max(0, flown - rtRoll.Incidents);
                int onTimeMilli = (int)Math.Round(1000.0 * cleanTrips / trips);
                route.ReliabilityMilli = Math.Clamp(
                    (int)Math.Round(_cfg.ReliabilityEmaAlpha * onTimeMilli + (1 - _cfg.ReliabilityEmaAlpha) * route.ReliabilityMilli),
                    0, 1000);
            }
            route.LastReconciledAt = dutyCapped ? now : route.LastReconciledAt.AddHours(trips * route.RoundTripHours);
            route.UpdatedAt = now;
            totalTrips += flown;
            grossIncome += income;
            totalIncidents += rtRoll.Incidents;
            totalEmpty += rtRoll.Empty;
            totalWeatheredOut += scrubbed;
            if (dutyCapped) dutyMaxed.Add(rtAircraft?.Tail ?? route.Name);
            int rtCrewSkillFlown = FlySkill(rtCrew); // fatigue-adjusted competence that flew (16d), before SharpenCrew/AccrueFatigue
            SharpenCrew(rtCrew, flown, now);
            AccrueFatigue(rtCrew, flown * route.RoundTripHours, Math.Max(0, elapsedH - flown * route.RoundTripHours)); // 16d
            repPullRaw += _cfg.OperatingRepCrewPullMilli(repStartMilli, Managed(rtCrewSkillFlown), flown); // Phase 11a; Managed = +org (16c)
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
        foreach (var loan in await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted).OrderBy(l => l.TakenAt).ThenBy(l => l.Id).ToListAsync(ct))
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
            grossIncome - totalFees - totalWages - totalRent - totalLoan - totalInsurance - totalRental + totalRentalRefund - totalFuel - totalRepair, totalIncidents, grounded, dutyMaxed, totalEmpty,
            loanWarnings, defaults, certLapsed, totalWeatheredOut, certExpiring, totalRental, rentalsExpiring, rentalsReturned, operatingRepDelta, totalFuel, totalRepair);
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
