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

        var cost = MaintenanceQuoteCents(inst);
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
