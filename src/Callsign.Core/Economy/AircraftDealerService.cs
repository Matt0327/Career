using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A priced, buyable aircraft type — with whether it's installed on this sim PC.</summary>
public sealed record AircraftOffer(AircraftType Type, AircraftPriceQuote Quote, bool OnDisk);

/// <summary>An owned airframe joined to its type, for the hangar view.</summary>
public sealed record OwnedAircraft(AircraftInstance Instance, AircraftType Type);

/// <summary>A pre-owned airframe on the used lot (Phase 7g): a real type, worn to some hours + condition,
/// priced below new. Regenerated deterministically from its seed, so the price is trusted at buy time.</summary>
public sealed record UsedListing(int Seed, Guid TypeId, string TypeName, string Category,
    double AirframeHours, int ConditionMilli, long PriceCents, long NewPriceCents);

/// <summary>
/// Buying and owning aircraft (Phase 2a). Every purchase debits the company through the ledger —
/// the airframe row and the money move in one transaction — so cash always reconciles.
/// </summary>
public sealed class AircraftDealerService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public AircraftDealerService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    /// <summary>Every known aircraft type, priced, cheapest first, flagged if installed on this PC.</summary>
    public async Task<IReadOnlyList<AircraftOffer>> GetOffersAsync(CancellationToken ct = default)
    {
        var types = await _db.AircraftTypes.ToListAsync(ct);
        var onDisk = (await _db.InstalledPackages.Where(i => i.IsOnDisk).Select(i => i.AircraftTypeId).ToListAsync(ct))
            .ToHashSet();
        return types
            .Select(t => new AircraftOffer(t, AircraftPricing.Quote(_cfg, t), onDisk.Contains(t.Id)))
            .OrderBy(o => o.Quote.TotalCents)
            .ToList();
    }

    /// <summary>A deterministic slate of used airframes for a seed — cheaper than new, but flown and worn.</summary>
    public async Task<IReadOnlyList<UsedListing>> GetUsedMarketAsync(int seed, CancellationToken ct = default)
    {
        var types = await _db.AircraftTypes.OrderBy(t => t.Key).ToListAsync(ct);
        if (types.Count == 0)
            return Array.Empty<UsedListing>();
        var rng = new Random(seed);
        var list = new List<UsedListing>(_cfg.UsedMarketCount);
        for (int i = 0; i < _cfg.UsedMarketCount; i++)
            list.Add(MakeUsedListing(types[rng.Next(types.Count)], rng.Next()));
        return list;
    }

    // One used airframe from a seed: worn hours + condition, priced off the current new value by condition.
    private UsedListing MakeUsedListing(AircraftType type, int seed)
    {
        var r = new Random(seed);
        double hours = 200 + r.Next(2_801);   // 200..3000 airframe hours flown by the last owner
        int cond = 50_000 + r.Next(45_001);   // 50%..95% condition — worn, but airworthy
        long newPrice = MarketValueCents(type);
        long price = (long)Math.Round(newPrice * _cfg.UsedPriceFactor(cond));
        return new UsedListing(seed, type.Id, type.CanonicalName, type.Category.ToString(), hours, cond, price, newPrice);
    }

    /// <summary>
    /// Buy a used airframe identified by (typeId, seed): the listing is regenerated server-side so the price
    /// and wear are trusted. It arrives at the listing's hours + condition (dealer-prepped: freshly inspected),
    /// cheaper than new but closer to needing service and worth less on resale. Idempotent via the key.
    /// </summary>
    public async Task<AircraftInstance> BuyUsedAsync(
        Guid companyId, Guid typeId, int seed, string atIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"buy-used:{idempotencyKey}";
        async Task<AircraftInstance?> PriorAsync()
        {
            if (dedupe is null) return null;
            var e = await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct);
            return e?.AircraftInstanceId is Guid id ? await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == id, ct) : null;
        }
        if (await PriorAsync() is { } replay) return replay;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == typeId, ct)
                   ?? throw new InvalidOperationException($"Aircraft type {typeId} not found.");

        var listing = MakeUsedListing(type, seed); // regenerate → trusted price + wear
        if (company.CashCents < listing.PriceCents)
            throw new InvalidOperationException(
                $"Not enough cash for the used {type.CanonicalName}: it costs {listing.PriceCents / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        var instance = new AircraftInstance
        {
            Id = Guid.NewGuid(), TypeId = typeId, CompanyId = companyId, Tail = NewTail(),
            Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = atIcao,
            HullConditionMilli = listing.ConditionMilli, EngineConditionMilli = listing.ConditionMilli,
            AirframeHours = listing.AirframeHours, MaintenanceHoursWatermark = listing.AirframeHours,
            Last100hHoursWatermark = listing.AirframeHours, LastAnnualAt = now, // dealer-prepped: freshly inspected
            PurchasePriceCents = listing.PriceCents, AcquiredAt = now, UpdatedAt = now,
        };
        _db.AircraftInstances.Add(instance);
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.AircraftPurchase, -(listing.PriceCents / 100m),
                $"Bought used {type.CanonicalName} ({instance.Tail}) — {listing.AirframeHours:F0} h, {listing.ConditionMilli / 1000}% condition",
                AircraftInstanceId: instance.Id, DedupeKey: dedupe ?? $"buy-used:{instance.Id}"),
        }, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await PriorAsync() is { } raced) return raced;
            throw;
        }
        return instance;
    }

    /// <summary>The company's owned airframes.</summary>
    public async Task<IReadOnlyList<OwnedAircraft>> GetHangarAsync(Guid companyId, CancellationToken ct = default)
    {
        var owned = await _db.AircraftInstances
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .OrderBy(a => a.AcquiredAt)
            .ToListAsync(ct);
        var typeIds = owned.Select(a => a.TypeId).Distinct().ToList();
        var types = await _db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        // Defensive: a missing type must never 500 the whole hangar (types are kept stable by the roster
        // upsert, so this shouldn't trigger — but one orphaned row shouldn't hide the rest of the fleet).
        return owned.Where(a => types.ContainsKey(a.TypeId))
            .Select(a => new OwnedAircraft(a, types[a.TypeId]))
            .ToList();
    }

    /// <summary>
    /// Buy an aircraft type at <paramref name="atIcao"/>: creates an owned airframe and debits the ledger.
    /// Pass a stable <paramref name="idempotencyKey"/> so a retried request replays the same purchase
    /// instead of buying (and charging) twice.
    /// </summary>
    public async Task<AircraftInstance> BuyAsync(
        Guid companyId, Guid typeId, string atIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"buy:{idempotencyKey}";

        // A retry of a purchase that already committed returns the same airframe — no second charge.
        async Task<AircraftInstance?> PriorAsync()
        {
            if (dedupe is null) return null;
            var e = await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct);
            return e?.AircraftInstanceId is Guid id
                ? await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == id, ct)
                : null;
        }
        if (await PriorAsync() is { } replay) return replay;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == typeId, ct)
                   ?? throw new InvalidOperationException($"Aircraft type {typeId} not found.");

        var quote = AircraftPricing.Quote(_cfg, type);
        if (company.CashCents < quote.TotalCents)
            throw new InvalidOperationException(
                $"Not enough cash for {type.CanonicalName}: it costs {quote.TotalCents / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        var instance = new AircraftInstance
        {
            Id = Guid.NewGuid(), TypeId = typeId, CompanyId = companyId, Tail = NewTail(),
            Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available,
            LocationIcao = atIcao, PurchasePriceCents = quote.TotalCents, AcquiredAt = now, UpdatedAt = now,
        };
        _db.AircraftInstances.Add(instance);

        // The airframe, the debit, and the cached balance all land in ONE transaction.
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.AircraftPurchase, -(quote.TotalCents / 100m),
                $"Bought {type.CanonicalName} ({instance.Tail})",
                AircraftInstanceId: instance.Id, DedupeKey: dedupe ?? $"buy:{instance.Id}"),
        }, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (dedupe is not null)
        {
            // A concurrent duplicate committed first (dedupe unique index / version token). Replay it.
            _db.ChangeTracker.Clear();
            if (await PriorAsync() is { } raced) return raced;
            throw;
        }
        return instance;
    }

    /// <summary>Discount maintenance / inspection when the tail is based at a company field with a shop (Phase 7g).</summary>
    private async Task<long> ApplyShopDiscountAsync(Guid companyId, string locationIcao, long cost, CancellationToken ct)
    {
        int level = await _db.Bases
            .Where(b => b.CompanyId == companyId && b.AirportIcao == locationIcao && b.IsActive && !b.IsDeleted)
            .Select(b => (int?)b.MaintenanceLevel).FirstOrDefaultAsync(ct) ?? 0;
        double disc = _cfg.MaintenanceShopDiscountPct(level);
        return disc <= 0 ? cost : (long)Math.Round(cost * (1 - disc));
    }

    /// <summary>What a maintenance service costs now: a base plus per-hour since the last service.</summary>
    public long MaintenanceQuoteCents(AircraftInstance inst)
        => _cfg.MaintenanceBaseCents
         + (long)Math.Round(Math.Max(0, inst.AirframeHours - inst.MaintenanceHoursWatermark) * _cfg.MaintenancePerHourCents);

    /// <summary>True once enough airframe hours have accrued since the last service.</summary>
    public bool MaintenanceDue(AircraftInstance inst)
        => inst.AirframeHours - inst.MaintenanceHoursWatermark >= _cfg.MaintenanceIntervalHours;

    /// <summary>
    /// Service an owned airframe: bill via the ledger, restore condition, reset the watermark. Pass a
    /// stable <paramref name="idempotencyKey"/> so a retried request replays instead of billing twice.
    /// </summary>
    public async Task<long> MaintainAsync(
        Guid companyId, Guid instanceId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"maint:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return -prior.AmountCents; // already serviced under this key — return the same cost, no re-charge

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var inst = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == instanceId && a.CompanyId == companyId, ct)
                   ?? throw new InvalidOperationException("Aircraft not found in your fleet.");

        long cost = await ApplyShopDiscountAsync(companyId, inst.LocationIcao, MaintenanceQuoteCents(inst), ct);
        if (company.CashCents < cost)
            throw new InvalidOperationException(
                $"Not enough cash: maintenance costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        // Without a client key, fall back to a state-derived key so a same-state double-submit still dedupes.
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Repair, -(cost / 100m), $"Maintenance on {inst.Tail}",
                AircraftInstanceId: inst.Id, DedupeKey: dedupe ?? $"maint:{inst.Id}:{inst.AirframeHours:F1}"),
        }, ct);
        inst.HullConditionMilli = 100_000;
        inst.EngineConditionMilli = 100_000;
        inst.MaintenanceHoursWatermark = inst.AirframeHours;
        inst.UpdatedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced)
                return -raced.AmountCents;
            throw;
        }
        return cost;
    }

    /// <summary>
    /// Whether an owned airframe may legally be dispatched, and why not (Phase 7e). A tail is grounded when
    /// its condition falls below the airworthy floor, or a 100-hour or annual inspection is overdue.
    /// <see cref="InspectionQuoteCents"/> is the cost to clear the DUE inspections (condition is cleared by
    /// maintenance instead). Also reports how much margin is left before each is due, for the hangar.
    /// </summary>
    public sealed record AirworthinessStatus(
        bool Airworthy, string? Reason, double HoursTo100h, int DaysToAnnual, long InspectionQuoteCents);

    public AirworthinessStatus Airworthiness(AircraftInstance inst) => AirworthinessOf(inst, _cfg, _clock.UtcNow);

    /// <summary>Airworthiness against a given config + clock — a pure function, so the autonomous reconcile
    /// loop can gate grounded tails without taking a dependency on this service.</summary>
    public static AirworthinessStatus AirworthinessOf(AircraftInstance inst, EconomyConfig cfg, DateTimeOffset now)
    {
        double hoursSince100h = inst.AirframeHours - inst.Last100hHoursWatermark;
        double hoursTo100h = Math.Max(0, cfg.HundredHourIntervalHours - hoursSince100h);
        var annualBase = inst.LastAnnualAt ?? inst.AcquiredAt ?? now;
        double daysSinceAnnual = (now - annualBase).TotalDays;
        int daysToAnnual = (int)Math.Ceiling(cfg.AnnualIntervalDays - daysSinceAnnual);

        bool due100h = hoursSince100h >= cfg.HundredHourIntervalHours;
        bool dueAnnual = daysSinceAnnual >= cfg.AnnualIntervalDays;
        int worstCond = Math.Min(inst.HullConditionMilli, inst.EngineConditionMilli);
        long quote = (due100h ? cfg.HundredHourInspectionCents : 0) + (dueAnnual ? cfg.AnnualInspectionCents : 0);

        string? reason =
            worstCond < cfg.AirworthyFloorMilli
                ? $"condition {worstCond / 1000}% is below the {cfg.AirworthyFloorMilli / 1000}% airworthy floor — service it"
            : due100h && dueAnnual ? "100-hour and annual inspections overdue"
            : due100h ? "100-hour inspection overdue"
            : dueAnnual ? "annual inspection overdue"
            : null;

        return new AirworthinessStatus(reason is null, reason, hoursTo100h, Math.Max(0, daysToAnnual), quote);
    }

    /// <summary>
    /// Return an airframe to service by clearing whatever inspections are due (100-hour and/or annual),
    /// billed through the ledger. Idempotent via <paramref name="idempotencyKey"/>. This does NOT restore
    /// condition — that is <see cref="MaintainAsync"/>.
    /// </summary>
    public async Task<long> InspectAsync(
        Guid companyId, Guid instanceId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"inspect:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return -prior.AmountCents;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var inst = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == instanceId && a.CompanyId == companyId, ct)
                   ?? throw new InvalidOperationException("Aircraft not found in your fleet.");

        var now = _clock.UtcNow;
        bool due100h = inst.AirframeHours - inst.Last100hHoursWatermark >= _cfg.HundredHourIntervalHours;
        bool dueAnnual = (now - (inst.LastAnnualAt ?? inst.AcquiredAt ?? now)).TotalDays >= _cfg.AnnualIntervalDays;
        long gross = (due100h ? _cfg.HundredHourInspectionCents : 0) + (dueAnnual ? _cfg.AnnualInspectionCents : 0);
        if (gross == 0)
            throw new InvalidOperationException("No inspection is due on this aircraft.");
        long cost = await ApplyShopDiscountAsync(companyId, inst.LocationIcao, gross, ct);
        if (company.CashCents < cost)
            throw new InvalidOperationException($"Not enough cash: the inspection costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Repair, -(cost / 100m), $"Inspection on {inst.Tail}",
                AircraftInstanceId: inst.Id, DedupeKey: dedupe ?? $"inspect:{inst.Id}:{inst.AirframeHours:F1}"),
        }, ct);
        if (due100h) inst.Last100hHoursWatermark = inst.AirframeHours;
        if (dueAnnual) inst.LastAnnualAt = now;
        inst.UpdatedAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return -raced.AmountCents;
            throw;
        }
        return cost;
    }

    /// <summary>Market sticker for a type (same formula the buy market and net-worth use).</summary>
    public long MarketValueCents(AircraftType type) => AircraftPricing.Quote(_cfg, type).TotalCents;

    /// <summary>
    /// What an airframe fetches if sold now: market × resale haircut × its worst condition —
    /// the exact figure <see cref="FinanceService.NetWorthAsync"/> already books it at as an asset.
    /// </summary>
    public long ResaleValueCents(AircraftInstance inst, AircraftType type)
    {
        double condition = Math.Min(inst.HullConditionMilli, inst.EngineConditionMilli) / 100_000.0;
        return (long)Math.Round(MarketValueCents(type) * _cfg.AircraftResaleFactor * condition);
    }

    /// <summary>
    /// Sell an owned airframe at its condition-adjusted resale value. Credits the ledger, retires the
    /// tail (soft-delete) and any policy on it. Refuses if the aircraft is busy or still flown by a
    /// standing order / route. Idempotent via <paramref name="idempotencyKey"/>.
    /// </summary>
    public async Task<long> SellAsync(
        Guid companyId, Guid instanceId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"sell:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return prior.AmountCents; // already sold under this key — replay the proceeds, no second credit

        var inst = await _db.AircraftInstances
            .FirstOrDefaultAsync(a => a.Id == instanceId && a.CompanyId == companyId && !a.IsDeleted, ct)
            ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (inst.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("This airframe is busy — it must be available (not in flight or reserved) to sell.");

        bool inUse = await _db.StandingOrders.AnyAsync(o => o.AircraftInstanceId == instanceId && o.IsActive && !o.IsDeleted, ct)
                  || await _db.Routes.AnyAsync(r => r.AircraftInstanceId == instanceId && r.Active && !r.IsDeleted, ct);
        if (inUse)
            throw new InvalidOperationException("Cancel the standing orders and routes this aircraft flies before selling it.");

        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId, ct)
                   ?? throw new InvalidOperationException("Aircraft type not found.");
        long proceeds = ResaleValueCents(inst, type);
        var now = _clock.UtcNow;

        // Positive AircraftPurchase posting keeps aircraft capital in one category (buy − sell nets cleanly).
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.AircraftPurchase, proceeds / 100m,
                $"Sold {type.CanonicalName} ({inst.Tail})",
                AircraftInstanceId: inst.Id, DedupeKey: dedupe ?? $"sell:{inst.Id}"),
        }, ct);

        inst.IsDeleted = true;
        inst.Availability = AircraftAvailability.Grounded;
        inst.UpdatedAt = now;

        foreach (var p in await _db.InsurancePolicies
                     .Where(p => p.AircraftInstanceId == instanceId && p.Active && !p.IsDeleted).ToListAsync(ct))
        {
            p.Active = false; p.IsDeleted = true; p.UpdatedAt = now;
        }

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return raced.AmountCents;
            throw;
        }
        return proceeds;
    }

    /// <summary>
    /// Ferry an idle airframe to another field. Charges a base + per-nm fee, and — because a ferry is a real
    /// leg — burns airframe hours and wears hull/engine accordingly. Idempotent via <paramref name="idempotencyKey"/>.
    /// Returns the fee charged.
    /// </summary>
    public async Task<long> RelocateAsync(
        Guid companyId, Guid instanceId, string destIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"ferry:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return -prior.AmountCents;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var inst = await _db.AircraftInstances
            .FirstOrDefaultAsync(a => a.Id == instanceId && a.CompanyId == companyId && !a.IsDeleted, ct)
            ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (inst.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("This airframe is busy — it must be available to ferry.");

        destIcao = (destIcao ?? "").Trim().ToUpperInvariant();
        if (destIcao.Length == 0) throw new InvalidOperationException("Pick a destination.");
        if (string.Equals(destIcao, inst.LocationIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The aircraft is already there.");

        var to = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                 ?? throw new InvalidOperationException($"Unknown airport {destIcao}.");
        var from = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == inst.LocationIcao, ct);
        double distanceNm = from is null ? 0 : GeoMath.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        long fee = _cfg.AircraftFerryBaseCents + (long)Math.Round(distanceNm * _cfg.AircraftFerryPerNmCents);
        if (company.CashCents < fee)
            throw new InvalidOperationException($"Not enough cash: the ferry costs {fee / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Fuel, -(fee / 100m),
                $"Ferry {inst.Tail}: {inst.LocationIcao} → {destIcao} ({distanceNm:F0} nm)",
                AircraftInstanceId: inst.Id, DedupeKey: dedupe ?? $"ferry:{inst.Id}:{destIcao}:{inst.AirframeHours:F1}"),
        }, ct);

        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == inst.TypeId, ct);
        double cruise = type?.CruiseKtas is int c && c > 60 ? c : 140;
        double hours = distanceNm / cruise;
        int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour);
        inst.AirframeHours += hours;
        inst.HullConditionMilli = Math.Max(0, inst.HullConditionMilli - wear);
        inst.EngineConditionMilli = Math.Max(0, inst.EngineConditionMilli - wear);
        inst.LocationIcao = destIcao;
        inst.UpdatedAt = now;

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return -raced.AmountCents;
            throw;
        }
        return fee;
    }

    // A friendly tail: "CS-" + 6 hex from a fresh guid (~16.7M space; collisions negligible per fleet).
    private static string NewTail() => "CS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
}
