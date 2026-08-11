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
        _db.InstalledPackages.RemoveRange(await _db.InstalledPackages.ToListAsync(ct));
        _db.AircraftTitleAliases.RemoveRange(await _db.AircraftTitleAliases.ToListAsync(ct));
        _db.AircraftTypes.RemoveRange(await _db.AircraftTypes.ToListAsync(ct));
        await _db.SaveChangesAsync(ct);

        var now = _clock.UtcNow;
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

            var type = new AircraftType
            {
                Id = Guid.NewGuid(),
                Key = key,
                CanonicalName = s?.CanonicalName ?? c!.CanonicalName,
                Manufacturer = c?.Manufacturer ?? s?.Manufacturer,
                IcaoTypeDesignator = s?.IcaoTypeDesignator ?? c?.IcaoTypeDesignator,
                IcaoModel = s?.IcaoModel,
                Category = category,
                UiTypeRole = s?.UiTypeRole,
                Seats = c?.Seats,
                UsefulLoadLbs = c?.UsefulLoadLbs,
                FuelCapacityLbs = c?.FuelCapacityLbs,
                CruiseKtas = c?.CruiseKtas,
                MinRunwayFt = c?.MinRunwayFt,
                Aliases = titles
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(t => new AircraftTitleAlias { Title = t, TitleNormalized = AircraftTitle.Normalize(t) })
                    .ToList(),
            };
            _db.AircraftTypes.Add(type);

            if (s is not null)
            {
                foreach (var loc in s.Locations)
                {
                    _db.InstalledPackages.Add(new InstalledPackage
                    {
                        Id = Guid.NewGuid(),
                        AircraftTypeId = type.Id,
                        Source = loc.Source,
                        PackageFolder = loc.PackageFolder,
                        AircraftFolder = loc.AircraftFolder,
                        IsOnDisk = true,
                        ScannedAt = now,
                    });
                }
            }
            else
            {
                // Curated-only: available to the player via cloud streaming, not a local file.
                _db.InstalledPackages.Add(new InstalledPackage
                {
                    Id = Guid.NewGuid(),
                    AircraftTypeId = type.Id,
                    Source = "Default2024",
                    PackageFolder = "(streamed default fleet)",
                    AircraftFolder = "",
                    IsOnDisk = false,
                    ScannedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return keys.Count;
    }
}
