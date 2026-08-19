using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A nearby airport you could open a base at, priced. Carries coordinates for the network map.</summary>
public sealed record BaseOffer(string Icao, string Name, string Kind, double DistanceNm, long OpenCents, long RentPerDayCents, double Latitude, double Longitude);

/// <summary>An open base, joined to its airport name + coordinates (for the self-rendered map).</summary>
public sealed record BaseView(Guid Id, string Icao, string Name, bool IsHome, long RentPerDayCents, double Latitude, double Longitude, int MaintenanceLevel, int FuelFarmLevel);

/// <summary>
/// Company bases (Phase 2e): open one at an airport for a setup fee + recurring rent (both ledger
/// debits). Your fleet lands fee-free at your own bases. Rent is billed in the reconcile pass.
/// </summary>
public sealed class BaseService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;
    private readonly AirportRepository _airports;

    public BaseService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg, AirportRepository airports)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
        _airports = airports;
    }

    public async Task<IReadOnlyList<BaseView>> GetBasesAsync(Guid companyId, CancellationToken ct = default)
    {
        var bases = await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .OrderByDescending(b => b.IsHome).ThenBy(b => b.AirportIcao).ToListAsync(ct);
        var icaos = bases.Select(b => b.AirportIcao).ToList();
        var airports = await _db.Airports.Where(a => icaos.Contains(a.Ident)).ToDictionaryAsync(a => a.Ident, ct);
        return bases.Select(b =>
        {
            airports.TryGetValue(b.AirportIcao, out var ap);
            return new BaseView(b.Id, b.AirportIcao, ap?.Name ?? b.AirportIcao, b.IsHome, b.RentPerDayCents,
                ap?.Latitude ?? 0, ap?.Longitude ?? 0, b.MaintenanceLevel, b.FuelFarmLevel);
        }).ToList();
    }

    /// <summary>
    /// Build or upgrade the maintenance shop at a base by one level (Phase 7g): a one-off capex debit that
    /// raises the servicing discount for tails based there (and adds a daily upkeep, billed at reconcile).
    /// Idempotent via <paramref name="idempotencyKey"/>. Returns the new level.
    /// </summary>
    public async Task<int> UpgradeMaintenanceShopAsync(
        Guid companyId, Guid baseId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"shop-upgrade:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is not null)
            return (await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId && x.CompanyId == companyId, ct))?.MaintenanceLevel ?? 0;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var b = await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId && x.CompanyId == companyId && x.IsActive && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Base not found.");
        if (b.MaintenanceLevel >= _cfg.MaxMaintenanceShopLevel)
            throw new InvalidOperationException("This maintenance shop is already at its top level.");

        int toLevel = b.MaintenanceLevel + 1;
        long cost = _cfg.MaintenanceShopUpgradeCents(toLevel);
        if (company.CashCents < cost)
            throw new InvalidOperationException($"Not enough cash: the shop upgrade costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.BaseRent, -(cost / 100m), $"Maintenance shop L{toLevel} at {b.AirportIcao}",
                BaseId: b.Id, DedupeKey: dedupe ?? $"shop-upgrade:{b.Id}:{toLevel}"),
        }, ct);
        b.MaintenanceLevel = toLevel;
        b.UpdatedAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            return (await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId, ct))?.MaintenanceLevel ?? toLevel;
        }
        return toLevel;
    }

    /// <summary>
    /// Build or upgrade the fuel farm at a base by one level (Phase 7g): a one-off capex debit that raises the
    /// fuel discount for legs departing there (and adds a daily upkeep, billed at reconcile). Idempotent via
    /// <paramref name="idempotencyKey"/>. Returns the new level.
    /// </summary>
    public async Task<int> UpgradeFuelFarmAsync(
        Guid companyId, Guid baseId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"fuelfarm-upgrade:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is not null)
            return (await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId && x.CompanyId == companyId, ct))?.FuelFarmLevel ?? 0;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var b = await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId && x.CompanyId == companyId && x.IsActive && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Base not found.");
        if (b.FuelFarmLevel >= _cfg.MaxFuelFarmLevel)
            throw new InvalidOperationException("This fuel farm is already at its top level.");

        int toLevel = b.FuelFarmLevel + 1;
        long cost = _cfg.FuelFarmUpgradeCents(toLevel);
        if (company.CashCents < cost)
            throw new InvalidOperationException($"Not enough cash: the fuel farm upgrade costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.BaseRent, -(cost / 100m), $"Fuel farm L{toLevel} at {b.AirportIcao}",
                BaseId: b.Id, DedupeKey: dedupe ?? $"fuelfarm-upgrade:{b.Id}:{toLevel}"),
        }, ct);
        b.FuelFarmLevel = toLevel;
        b.UpdatedAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            return (await _db.Bases.FirstOrDefaultAsync(x => x.Id == baseId, ct))?.FuelFarmLevel ?? toLevel;
        }
        return toLevel;
    }

    /// <summary>Nearby real-ICAO airports (around the home base) you don't already base at, priced.</summary>
    public async Task<IReadOnlyList<BaseOffer>> GetCandidatesAsync(Guid companyId, CancellationToken ct = default)
    {
        var existing = (await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive).Select(b => b.AirportIcao).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var homeIcao = await _db.Bases.Where(b => b.CompanyId == companyId && b.IsHome).Select(b => b.AirportIcao).FirstOrDefaultAsync(ct)
                       ?? existing.FirstOrDefault();
        if (homeIcao is null)
            return Array.Empty<BaseOffer>();
        var home = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == homeIcao, ct);
        if (home is null)
            return Array.Empty<BaseOffer>();

        var near = await _airports.WithinRadiusAsync(home.Latitude, home.Longitude, 300, ct);
        return near
            .Where(x => !existing.Contains(x.Airport.Ident)
                        && !string.IsNullOrWhiteSpace(x.Airport.IcaoCode)
                        && string.Equals(x.Airport.Ident, x.Airport.IcaoCode, StringComparison.OrdinalIgnoreCase)
                        && x.Airport.Kind is AirportKind.SmallAirport or AirportKind.MediumAirport or AirportKind.LargeAirport)
            .Take(8)
            .Select(x => new BaseOffer(x.Airport.Ident, x.Airport.Name, x.Airport.Kind.ToString(), x.DistanceNm,
                _cfg.BaseOpenCents(x.Airport.Kind), _cfg.BaseRentPerDayCents(x.Airport.Kind), x.Airport.Latitude, x.Airport.Longitude))
            .ToList();
    }

    /// <summary>
    /// Open a base at an airport: pay the setup fee via the ledger and start the rent clock. Pass a stable
    /// <paramref name="idempotencyKey"/> so a retried request replays the same base instead of billing twice.
    /// </summary>
    public async Task<Base> OpenBaseAsync(Guid companyId, string airportIcao, string? idempotencyKey = null, CancellationToken ct = default)
    {
        string? dedupe = idempotencyKey is null ? null : $"base-open:{idempotencyKey}";

        async Task<Base?> PriorAsync()
        {
            if (dedupe is null) return null;
            var e = await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct);
            return e?.BaseId is Guid id ? await _db.Bases.FirstOrDefaultAsync(b => b.Id == id, ct) : null;
        }
        if (await PriorAsync() is { } replay) return replay;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        if (await _db.Bases.AnyAsync(b => b.CompanyId == companyId && b.AirportIcao == airportIcao && b.IsActive, ct))
            throw new InvalidOperationException($"You already have a base at {airportIcao}.");
        var airport = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == airportIcao, ct)
                      ?? throw new InvalidOperationException($"Airport {airportIcao} is unknown.");

        long setup = _cfg.BaseOpenCents(airport.Kind);
        if (company.CashCents < setup)
            throw new InvalidOperationException($"Not enough cash: opening a base at {airportIcao} costs {setup / 100m:C0}, you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        var b = new Base
        {
            Id = Guid.NewGuid(), CompanyId = companyId, AirportIcao = airportIcao, IsHome = false,
            RentPerDayCents = _cfg.BaseRentPerDayCents(airport.Kind), OpenedAt = now, LastRentBilledAt = now,
            IsActive = true, UpdatedAt = now,
        };
        _db.Bases.Add(b);
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.BaseRent, -(setup / 100m), $"Opened base at {airportIcao}",
                BaseId: b.Id, DedupeKey: dedupe ?? $"base-open:{b.Id}"),
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
        return b;
    }
}
