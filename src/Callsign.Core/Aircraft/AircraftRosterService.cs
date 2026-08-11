using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Aircraft;

/// <summary>
/// Persists a scan result into the roster: <see cref="AircraftType"/> + title aliases (shared
/// identity) and <see cref="InstalledPackage"/> (machine-local). A rescan replaces the previous
/// roster wholesale, since it is derived from the scan.
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

    public async Task<int> ReplaceAsync(IReadOnlyList<ScannedAircraftType> scanned, CancellationToken ct = default)
    {
        _db.InstalledPackages.RemoveRange(await _db.InstalledPackages.ToListAsync(ct));
        _db.AircraftTitleAliases.RemoveRange(await _db.AircraftTitleAliases.ToListAsync(ct));
        _db.AircraftTypes.RemoveRange(await _db.AircraftTypes.ToListAsync(ct));
        await _db.SaveChangesAsync(ct);

        var now = _clock.UtcNow;
        foreach (var s in scanned)
        {
            var type = new AircraftType
            {
                Id = Guid.NewGuid(),
                Key = s.Key,
                CanonicalName = s.CanonicalName,
                Manufacturer = s.Manufacturer,
                IcaoTypeDesignator = s.IcaoTypeDesignator,
                IcaoModel = s.IcaoModel,
                Category = s.Category,
                UiTypeRole = s.UiTypeRole,
                Aliases = s.Titles
                    .Select(t => new AircraftTitleAlias { Title = t, TitleNormalized = AircraftTitle.Normalize(t) })
                    .ToList(),
            };
            _db.AircraftTypes.Add(type);

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

        await _db.SaveChangesAsync(ct);
        return scanned.Count;
    }
}
