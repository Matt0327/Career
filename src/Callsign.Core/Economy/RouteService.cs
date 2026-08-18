using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>
/// Routes (Phase 4d): named, scheduled lines between two of your bases, flown autonomously by an owned
/// aircraft + staff pilot. The reward is economy-frozen at creation from the chosen mission's economics;
/// trips are booked in the reconcile pass (fee-free, since both ends are your bases). Cancelling frees the
/// aircraft. Reputation-gated / illicit missions can't be run as routes.
/// </summary>
public sealed class RouteService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public RouteService(CallsignDbContext db, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
    }

    public Task<List<Route>> GetRoutesAsync(Guid companyId, CancellationToken ct = default)
        => _db.Routes.Where(r => r.CompanyId == companyId && r.Active && !r.IsDeleted).OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<Route> CreateRouteAsync(
        Guid companyId, string? name, string originIcao, string destIcao, Guid aircraftId, Guid staffId, MissionType mission, CancellationToken ct = default)
    {
        if (string.Equals(originIcao, destIcao, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A route needs two different airports.");

        var bases = (await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .Select(b => b.AirportIcao).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!bases.Contains(originIcao) || !bases.Contains(destIcao))
            throw new InvalidOperationException("Both ends of a route must be your bases.");

        var def = MissionCatalog.TryDef(mission)
                  ?? throw new InvalidOperationException("Unknown mission type.");
        if (def.MinReputationMilli != 0 || def.ReputationMilliReward < 0)
            throw new InvalidOperationException($"{def.DisplayName} work can't be run as a scheduled route.");

        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftId && a.CompanyId == companyId && !a.IsDeleted, ct)
                       ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (aircraft.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("That aircraft isn't available.");
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        // One pilot flies one line, so the per-line FTL duty cap equals the crew's real daily limit (Phase 7f).
        bool alreadyFlying = await _db.StandingOrders.AnyAsync(o => o.StaffId == staffId && o.IsActive && !o.IsDeleted, ct)
                          || await _db.Routes.AnyAsync(r => r.StaffId == staffId && r.Active && !r.IsDeleted, ct);
        if (alreadyFlying)
            throw new InvalidOperationException($"{staff.Name} already flies another line — assign a different pilot or hire one.");

        var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == originIcao, ct)
                   ?? throw new InvalidOperationException($"Airport {originIcao} is unknown.");
        var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                   ?? throw new InvalidOperationException($"Airport {destIcao} is unknown.");

        double dist = GeoMath.DistanceNm(oAir.Latitude, oAir.Longitude, dAir.Latitude, dAir.Longitude);
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct);
        // Type rating: the crew must be experienced enough for the aircraft's category (Phase 7f).
        int need = _cfg.MinSkillMilliForCategory(type?.Category ?? AircraftCategory.Unknown);
        if (staff.SkillMilli < need)
            throw new InvalidOperationException($"{staff.Name} ({staff.SkillMilli / 1000}%) isn't rated for the {type!.Category} — assign a pilot at {need / 1000}%+.");
        double cruise = type?.CruiseKtas ?? 150;
        double rtHours = 2 * dist / Math.Max(60, cruise);
        long baseReward = def.CarriesPassengers
            ? _cfg.PaxRewardCents(dist, (_cfg.MinPax + _cfg.MaxPax) / 2)
            : _cfg.CargoRewardCents(dist, (_cfg.MinCargoWeightLbs + _cfg.MaxCargoWeightLbs) / 2);
        long reward = (long)Math.Round(baseReward * def.RewardMult); // economy-frozen, never player-set

        var now = _clock.UtcNow;
        var route = new Route
        {
            Id = Guid.NewGuid(), CompanyId = companyId,
            Name = string.IsNullOrWhiteSpace(name) ? $"{originIcao}–{destIcao} {def.DisplayName}" : name!.Trim(),
            OriginIcao = originIcao, DestIcao = destIcao, Mission = mission, DistanceNm = dist,
            RoundTripHours = rtHours, RewardPerTripCents = reward, AircraftInstanceId = aircraftId, StaffId = staffId,
            Active = true, StartedAt = now, LastReconciledAt = now, UpdatedAt = now,
        };
        _db.Routes.Add(route);
        aircraft.Availability = AircraftAvailability.Reserved; // held by the route
        aircraft.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return route;
    }

    public async Task CancelRouteAsync(Guid companyId, Guid routeId, CancellationToken ct = default)
    {
        var route = await _db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.CompanyId == companyId && r.Active, ct);
        if (route is null)
            return;
        route.Active = false;
        route.UpdatedAt = _clock.UtcNow;
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == route.AircraftInstanceId, ct);
        if (aircraft is not null)
        {
            aircraft.Availability = AircraftAvailability.Available;
            aircraft.UpdatedAt = _clock.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
