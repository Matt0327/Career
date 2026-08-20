using Callsign.Core.Data;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>One client relationship, valued at its current (decayed) standing.</summary>
public sealed record ClientView(
    string Name, string HomeIcao, int LoyaltyMilli, int JobsCompleted, int JobsFailed,
    DateTimeOffset? LastJobAt, DateTimeOffset FirstSeenAt);

/// <summary>
/// The face of the client system (Phase 8d-2): your named clients and where each relationship stands right
/// now — loyalty decayed to the present, so a bond you've neglected reads as cooled, not frozen at its peak.
/// Read-only; loyalty moves only through settlement (Phase 8d-1).
/// </summary>
public sealed class ClientService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public ClientService(CallsignDbContext db, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
    }

    public async Task<IReadOnlyList<ClientView>> GetClientsAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var clients = await _db.Clients.Where(c => c.CompanyId == companyId).ToListAsync(ct);
        return clients
            .Select(c => new ClientView(c.Name, c.HomeIcao,
                _cfg.DecayedLoyaltyMilli(c.LoyaltyMilli, now - (c.LastJobAt ?? c.UpdatedAt)),
                c.JobsCompleted, c.JobsFailed, c.LastJobAt, c.FirstSeenAt))
            .OrderByDescending(v => v.LoyaltyMilli)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
