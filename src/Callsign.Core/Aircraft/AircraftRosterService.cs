using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Aircraft;

/// <summary>
/// Builds the aircraft roster from a scan plus a curated default-fleet catalog, and persists it:
/// <see cref="AircraftType"/> + title aliases (shared identity) and <see cref="InstalledPackage"/>
/// (machine-local scan state). A scanned community aircraft that shares an ICAO type with a curated
/// entry is enriched with the curated specs; curated-only aircraft are recorded as the streamed
/// default fleet. A rebuild replaces the previous roster wholesale, since it is derived.
/// </summary>
public sealed class AircraftRosterService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;

    public AircraftRosterService(CallsignDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Persist a scan-only roster (no curated fleet). Mostly for tests.</summary>
    public Task<int> ReplaceAsync(IReadOnlyList<ScannedAircraftType> scanned, CancellationToken ct = default)
        => RebuildAsync(scanned, Array.Empty<CuratedAircraft>(), ct);

    /// <summary>Merge a scan with the curated fleet and persist the combined roster.</summary>
    public async Task<int> RebuildAsync(
        IReadOnlyList<ScannedAircraftType> scanned,
        IReadOnlyList<CuratedAircraft> curated,
        CancellationToken ct = default)
    {
        // Aliases + install state are derived/machine-local and unreferenced — safe to rebuild wholesale.
        // AircraftType rows, however, are referenced by OWNED aircraft (AircraftInstance.TypeId), so they
        // are UPSERTED by Key: a rescan keeps each type's Id stable, and never deletes a type you own.
        _db.InstalledPackages.RemoveRange(await _db.InstalledPackages.ToListAsync(ct));
        _db.AircraftTitleAliases.RemoveRange(await _db.AircraftTitleAliases.ToListAsync(ct));
        await _db.SaveChangesAsync(ct);

        var now = _clock.UtcNow;
        var existing = (await _db.AircraftTypes.ToListAsync(ct))
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var scannedByKey = scanned.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var curatedByKey = curated.ToDictionary(c => c.IcaoTypeDesignator.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        var keys = new HashSet<string>(scannedByKey.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(curatedByKey.Keys);

        foreach (var key in keys)
        {
            scannedByKey.TryGetValue(key, out var s);
            curatedByKey.TryGetValue(key, out var c);

            var titles = new List<string>();
            if (s is not null) titles.AddRange(s.Titles);
            if (c is not null) titles.AddRange(c.Aliases);

            var category = c is not null && c.Category != AircraftCategory.Unknown
                ? c.Category
                : s?.Category ?? AircraftCategory.Unknown;

            if (!existing.TryGetValue(key, out var type))
            {
                type = new AircraftType { Id = Guid.NewGuid(), Key = key };
                _db.AircraftTypes.Add(type);
            }
            // Refresh identity + specs in place, preserving the (referenced) Id.
            type.CanonicalName = s?.CanonicalName ?? c!.CanonicalName;
            type.Manufacturer = c?.Manufacturer ?? s?.Manufacturer;
            type.IcaoTypeDesignator = s?.IcaoTypeDesignator ?? c?.IcaoTypeDesignator;
            type.IcaoModel = s?.IcaoModel;
            type.Category = category;
            type.UiTypeRole = s?.UiTypeRole;
            type.Seats = c?.Seats;
            type.UsefulLoadLbs = c?.UsefulLoadLbs;
            type.FuelCapacityLbs = c?.FuelCapacityLbs;
            type.CruiseKtas = c?.CruiseKtas;
            type.MinRunwayFt = c?.MinRunwayFt;

            foreach (var t in titles.Distinct(StringComparer.OrdinalIgnoreCase))
                _db.AircraftTitleAliases.Add(new AircraftTitleAlias
                {
                    AircraftTypeId = type.Id,
                    Title = t,
                    TitleNormalized = AircraftTitle.Normalize(t),
                });

            if (s is not null)
            {
                foreach (var loc in s.Locations)
                    _db.InstalledPackages.Add(new InstalledPackage
                    {
                        Id = Guid.NewGuid(), AircraftTypeId = type.Id, Source = loc.Source,
                        PackageFolder = loc.PackageFolder, AircraftFolder = loc.AircraftFolder,
                        IsOnDisk = true, ScannedAt = now,
                    });
            }
            else
            {
                // Curated-only: available to the player via cloud streaming, not a local file.
                _db.InstalledPackages.Add(new InstalledPackage
                {
                    Id = Guid.NewGuid(), AircraftTypeId = type.Id, Source = "Default2024",
                    PackageFolder = "(streamed default fleet)", AircraftFolder = "", IsOnDisk = false, ScannedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return keys.Count;
    }

    /// <summary>
    /// Additively ensure every curated catalog type EXISTS as an <see cref="AircraftType"/>, without a full
    /// rebuild. A full <see cref="RebuildAsync"/> only runs at game creation, so a curated aircraft added to
    /// the catalog after a career started (e.g. a new halo type) would otherwise never appear. This adds only
    /// the MISSING ones — it never touches existing types, scan state, or aliases — so it is safe to run on
    /// every startup and is idempotent. Returns how many new types were added.
    /// </summary>
    public async Task<int> EnsureCuratedTypesAsync(IReadOnlyList<CuratedAircraft> curated, CancellationToken ct = default)
    {
        var have = (await _db.AircraftTypes.Select(t => t.Key).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = _clock.UtcNow;
        int added = 0;
        foreach (var c in curated)
        {
            var key = c.IcaoTypeDesignator.ToUpperInvariant();
            if (have.Contains(key)) continue;
            var type = new AircraftType
            {
                Id = Guid.NewGuid(), Key = key,
                CanonicalName = c.CanonicalName, Manufacturer = c.Manufacturer,
                IcaoTypeDesignator = c.IcaoTypeDesignator, Category = c.Category,
                Seats = c.Seats, UsefulLoadLbs = c.UsefulLoadLbs, FuelCapacityLbs = c.FuelCapacityLbs,
                CruiseKtas = c.CruiseKtas, MinRunwayFt = c.MinRunwayFt,
            };
            _db.AircraftTypes.Add(type);
            foreach (var t in c.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                _db.AircraftTitleAliases.Add(new AircraftTitleAlias
                {
                    AircraftTypeId = type.Id, Title = t, TitleNormalized = AircraftTitle.Normalize(t),
                });
            _db.InstalledPackages.Add(new InstalledPackage
            {
                Id = Guid.NewGuid(), AircraftTypeId = type.Id, Source = "Default2024",
                PackageFolder = "(streamed default fleet)", AircraftFolder = "", IsOnDisk = false, ScannedAt = now,
            });
            have.Add(key);
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }
}
