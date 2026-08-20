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

/// <summary>A rentable aircraft type (Phase 9f-1): its holding + usage rates and the escrow deposit.</summary>
public sealed record RentalOffer(Guid TypeId, string TypeName, string Category, long StickerCents,
    long DailyHoldingCents, long FlightHourCents, long DepositCents, int TermDays);

/// <summary>A preview of what returning a rental would cost/refund now — no mutation (Phase 9f-1).</summary>
public sealed record RentalReturnQuote(long FinalRentCents, long DamageCents, string? DamageReason, long RefundCents);

/// <summary>An active rental joined to its tail + type, with accrued rent and the live projected refund.</summary>
public sealed record ActiveRental(RentalAgreement Agreement, AircraftInstance Tail, AircraftType Type,
    long AccruedRentCents, int DaysLeft, RentalReturnQuote Projected);

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
        // Only an OWNED tail (Phase 9f): the lessor maintains a rental. Without this a renter could wreck a tail,
        // service it back to 100% for a flat fee, and reclaim the whole damage deposit — nulling the mechanic.
        if (inst.Ownership != OwnershipKind.Owned)
            throw new InvalidOperationException("You can only service an aircraft you own — the lessor maintains a rental.");

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
        if (inst.Ownership != OwnershipKind.Owned) // Phase 9f: the lessor handles a rental's airworthiness
            throw new InvalidOperationException("You can only inspect an aircraft you own — the lessor maintains a rental.");

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
        // You can only sell a tail you OWN (Phase 9f master invariant): a rented/leased tail has no equity to
        // sell — it goes back to its owner via Return, never onto the block.
        if (inst.Ownership != OwnershipKind.Owned)
            throw new InvalidOperationException("You can only sell an aircraft you own — return the rental instead.");
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
        if (inst.Ownership != OwnershipKind.Owned) // Phase 9f: a rental is hand-fly-only; fly it where you need it
            throw new InvalidOperationException("You can only ferry an aircraft you own — fly the rental there yourself.");
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

    // ── Aircraft rental (Phase 9f-1) ──────────────────────────────────────────────────────────────
    // Rent a non-owned tail, fly it by hand, return it. The deposit escrows the renter's abnormal-damage
    // liability; a holding fee + per-flight-hour usage rent are billed at reconcile; on return the deposit
    // is refunded less any real damage BEYOND ordinary hours-depreciation (so coach-band flying is free).

    /// <summary>Resale value of a tail AT a hypothetical hull/engine condition — the same market × haircut ×
    /// worst-condition formula <see cref="ResaleValueCents"/> uses, for the return-damage waterfall (9f-1).</summary>
    public long ValueAtCondition(AircraftType type, int hullMilli, int engineMilli)
        => ValueAtConditionCents(_cfg, type, hullMilli, engineMilli);

    private static long ValueAtConditionCents(EconomyConfig cfg, AircraftType type, int hullMilli, int engineMilli)
    {
        double condition = Math.Clamp(Math.Min(hullMilli, engineMilli), 0, 100_000) / 100_000.0;
        return (long)Math.Round(AircraftPricing.Quote(cfg, type).TotalCents * cfg.AircraftResaleFactor * condition);
    }

    /// <summary>Every rentable type at a field, priced (holding/day + usage/flight-hour + deposit), cheapest first.</summary>
    public async Task<IReadOnlyList<RentalOffer>> GetRentalOffersAsync(CancellationToken ct = default)
    {
        var types = await _db.AircraftTypes.ToListAsync(ct);
        return types
            .Select(t =>
            {
                long sticker = MarketValueCents(t);
                return new RentalOffer(t.Id, t.CanonicalName, t.Category.ToString(), sticker,
                    _cfg.RentHoldingPerDayCents(sticker), _cfg.RentFlightHourCents(sticker),
                    _cfg.RentDepositCents(sticker), _cfg.RentTermDefaultDays);
            })
            .OrderBy(o => o.StickerCents)
            .ToList();
    }

    /// <summary>
    /// Rent a fresh tail of a type at <paramref name="atIcao"/>: escrows a deposit (real cash), delivers a
    /// serviced <see cref="OwnershipKind.Rented"/> airframe you fly by hand, and opens a <see cref="RentalAgreement"/>.
    /// The sticker is NEVER debited — you did not buy it. Idempotent via <paramref name="idempotencyKey"/>.
    /// </summary>
    public async Task<AircraftInstance> RentAsync(
        Guid companyId, Guid typeId, string atIcao, int termDays = 0, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"rent-open:{idempotencyKey}";
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

        long sticker = MarketValueCents(type);
        long deposit = _cfg.RentDepositCents(sticker); // the escrow actually leaves cash, so check it
        if (company.CashCents < deposit)
            throw new InvalidOperationException(
                $"Not enough cash to rent the {type.CanonicalName}: the deposit is {deposit / 100m:C0}, you have {company.Cash:C0}.");

        int term = Math.Clamp(termDays <= 0 ? _cfg.RentTermDefaultDays : termDays, 1, _cfg.RentMaxTermDays);
        var now = _clock.UtcNow;
        var instance = new AircraftInstance
        {
            Id = Guid.NewGuid(), TypeId = typeId, CompanyId = companyId, Tail = NewTail(),
            Ownership = OwnershipKind.Rented, Availability = AircraftAvailability.Available, LocationIcao = atIcao,
            HullConditionMilli = _cfg.RentalDeliveryConditionMilli, EngineConditionMilli = _cfg.RentalDeliveryConditionMilli,
            AirframeHours = 0, MaintenanceHoursWatermark = 0, Last100hHoursWatermark = 0, LastAnnualAt = now, // dealer-prepped, flyable now
            PurchasePriceCents = null, AcquiredAt = now, UpdatedAt = now, // you did not buy it → no purchase basis
        };
        _db.AircraftInstances.Add(instance);

        var agreement = new RentalAgreement
        {
            Id = Guid.NewGuid(), CompanyId = companyId, AircraftInstanceId = instance.Id,
            Kind = RentalKind.Rental, Status = RentalStatus.Active,
            HoursAtPickup = 0, HullMilliAtPickup = instance.HullConditionMilli, EngineMilliAtPickup = instance.EngineConditionMilli,
            DepositCents = deposit, HoldingPerDayCents = _cfg.RentHoldingPerDayCents(sticker), FlightHourRateCents = _cfg.RentFlightHourCents(sticker),
            LastRentBilledAt = now, HoursLastBilled = 0, StartedAt = now, ExpiresAt = now.AddDays(term), UpdatedAt = now,
        };
        _db.RentalAgreements.Add(agreement);

        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.AircraftRental, -(deposit / 100m),
                $"Rental deposit — {type.CanonicalName} ({instance.Tail})",
                LedgerRefType.Rental, agreement.Id.ToString(), AircraftInstanceId: instance.Id, DedupeKey: dedupe ?? $"rental-deposit:{agreement.Id}"),
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

    // The final-rent + damage waterfall for a return, as a PURE computation so the preview and the real return
    // share one source of truth. Damage = resale-value shortfall BELOW the ordinary hours-depreciation line
    // (usage rent already paid for routine wear), clamped to the deposit — so only abnormal damage bills, and
    // never more than the escrow.
    private static (long Holding, long Usage, long Damage, string? Reason, long Refund) ComputeReturn(
        EconomyConfig cfg, RentalAgreement ag, AircraftInstance tail, AircraftType type, DateTimeOffset now)
    {
        long holding = (long)Math.Round(Math.Max(0, (now - ag.LastRentBilledAt).TotalDays) * ag.HoldingPerDayCents);
        double usageHours = Math.Max(0, tail.AirframeHours - ag.HoursLastBilled);
        long usage = (long)Math.Round(usageHours * ag.FlightHourRateCents);

        // Expected wear = ordinary hours wear PLUS the Fun-Dial handling allowance (L9): a firm-but-acceptable
        // landing and minor engine wear are expected and free; only wear past this line — a real slam, or gross
        // engine abuse (9e) — bites the deposit. This is what makes "coach-band flying is free" true on the
        // telemetry path (settlement also subtracts landing + engine-abuse wear, not just hours).
        int expectedWear = (int)Math.Round(Math.Max(0, tail.AirframeHours - ag.HoursAtPickup)
            * (cfg.ConditionWearMilliPerHour + cfg.RentOrdinaryHandlingWearMilliPerHour));
        int expHull = Math.Max(0, ag.HullMilliAtPickup - expectedWear);
        int expEngine = Math.Max(0, ag.EngineMilliAtPickup - expectedWear);
        long valueExpected = ValueAtConditionCents(cfg, type, expHull, expEngine);
        long valueActual = ValueAtConditionCents(cfg, type, tail.HullConditionMilli, tail.EngineConditionMilli);
        long damage = Math.Clamp(valueExpected - valueActual, 0, ag.DepositCents);
        string? reason = damage > 0
            ? $"returned at {Math.Min(tail.HullConditionMilli, tail.EngineConditionMilli) / 1000}% vs the {Math.Min(expHull, expEngine) / 1000}% expected for the hours flown"
            : null;
        long refund = ag.DepositCents - damage; // return-of-principal, never < 0 (damage ≤ deposit)
        return (holding, usage, damage, reason, refund);
    }

    /// <summary>Preview what returning a rental costs/refunds NOW, without mutating anything (Law 4, warned-first).</summary>
    public async Task<RentalReturnQuote?> ReturnQuoteAsync(Guid companyId, Guid agreementId, CancellationToken ct = default)
    {
        var ag = await _db.RentalAgreements.FirstOrDefaultAsync(a => a.Id == agreementId && a.CompanyId == companyId && a.Status == RentalStatus.Active && !a.IsDeleted, ct);
        if (ag is null) return null;
        var tail = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == ag.AircraftInstanceId, ct);
        var type = tail is null ? null : await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == tail.TypeId, ct);
        if (tail is null || type is null) return null;
        var (holding, usage, damage, reason, refund) = ComputeReturn(_cfg, ag, tail, type, _clock.UtcNow);
        return new RentalReturnQuote(holding + usage, damage, reason, refund);
    }

    /// <summary>
    /// Settle a return: bill the final holding + usage rent, withhold any damage beyond ordinary wear from the
    /// deposit, refund the rest, and retire the tail + agreement. STAGES onto the context (does not save) so
    /// both the endpoint and the reconcile auto-return can commit it in their own transaction. Returns the
    /// refund AND the rent billed (so reconcile can fold an auto-return into its digest). The
    /// <c>rental-return:{id}</c> posting is always written, so the return is idempotent.
    /// </summary>
    internal static async Task<(long Refund, long RentBilled)> SettleReturnAsync(
        CallsignDbContext db, LedgerService ledger, EconomyConfig cfg,
        RentalAgreement ag, AircraftInstance tail, AircraftType type, DateTimeOffset now, CancellationToken ct)
    {
        var (holding, usage, damage, _, refund) = ComputeReturn(cfg, ag, tail, type, now);
        var stamp = ag.LastRentBilledAt.UtcTicks; // one stamp keys both fees; both watermarks advance together
        var postings = new List<LedgerPosting>();
        if (holding > 0)
            postings.Add(new(LedgerCategory.AircraftRental, -(holding / 100m), $"Rental holding — {tail.Tail}",
                LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"rental-hold:{ag.Id}:{stamp}"));
        if (usage > 0)
            postings.Add(new(LedgerCategory.AircraftRental, -(usage / 100m), $"Rental usage — {tail.Tail}",
                LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"rental-use:{ag.Id}:{stamp}"));
        // Refund = deposit − damage (return-of-principal). Always posted, even at 0, as the idempotency marker.
        postings.Add(new(LedgerCategory.AircraftRental, refund / 100m,
            damage > 0 ? $"Deposit refund less {damage / 100m:C0} damage — {tail.Tail}" : $"Deposit refund — {tail.Tail}",
            LedgerRefType.Rental, ag.Id.ToString(), AircraftInstanceId: tail.Id, DedupeKey: $"rental-return:{ag.Id}"));
        await ledger.StageBatchAsync(ag.CompanyId, postings, ct);

        ag.LastRentBilledAt = now;
        ag.HoursLastBilled = tail.AirframeHours;
        ag.Status = RentalStatus.Returned;
        ag.UpdatedAt = now;
        tail.IsDeleted = true;               // goes back to the owner — off your books
        tail.Availability = AircraftAvailability.Grounded;
        tail.UpdatedAt = now;
        return (refund, holding + usage);
    }

    /// <summary>Return a rented tail: settles the final rent + damage and refunds the deposit. Idempotent.</summary>
    public async Task<long> ReturnAsync(Guid companyId, Guid agreementId, CancellationToken ct = default)
    {
        string dedupe = $"rental-return:{agreementId}";
        if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return prior.AmountCents; // already returned — replay the refund, no second settlement

        var ag = await _db.RentalAgreements.FirstOrDefaultAsync(a => a.Id == agreementId && a.CompanyId == companyId && !a.IsDeleted, ct)
                 ?? throw new InvalidOperationException("Rental not found.");
        if (ag.Status != RentalStatus.Active)
            throw new InvalidOperationException("This rental has already been closed.");
        var tail = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == ag.AircraftInstanceId, ct)
                   ?? throw new InvalidOperationException("Rented aircraft not found.");
        if (tail.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("Finish this aircraft's flight before returning it.");
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == tail.TypeId, ct)
                   ?? throw new InvalidOperationException("Aircraft type not found.");

        long refund = (await SettleReturnAsync(_db, _ledger, _cfg, ag, tail, type, _clock.UtcNow, ct)).Refund;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced) return raced.AmountCents;
            throw;
        }
        return refund;
    }

    /// <summary>The company's active rentals, joined to tail + type, with accrued rent + the live return quote.</summary>
    public async Task<IReadOnlyList<ActiveRental>> GetActiveRentalsAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var ags = await _db.RentalAgreements
            .Where(r => r.CompanyId == companyId && r.Status == RentalStatus.Active && !r.IsDeleted)
            .OrderBy(r => r.StartedAt).ToListAsync(ct);
        var result = new List<ActiveRental>(ags.Count);
        foreach (var ag in ags)
        {
            var tail = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == ag.AircraftInstanceId, ct);
            var type = tail is null ? null : await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == tail.TypeId, ct);
            if (tail is null || type is null) continue;
            var (holding, usage, damage, reason, refund) = ComputeReturn(_cfg, ag, tail, type, now);
            int daysLeft = Math.Max(0, (int)Math.Ceiling((ag.ExpiresAt - now).TotalDays));
            result.Add(new ActiveRental(ag, tail, type, holding + usage, daysLeft,
                new RentalReturnQuote(holding + usage, damage, reason, refund)));
        }
        return result;
    }

    // A friendly tail: "CS-" + 6 hex from a fresh guid (~16.7M space; collisions negligible per fleet).
    private static string NewTail() => "CS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
}
