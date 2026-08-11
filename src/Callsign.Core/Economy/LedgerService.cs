using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>
/// The one sanctioned way money moves. Every monetary change writes a
/// <see cref="LedgerEntry"/> and updates the pilot's cached balance in the same
/// transaction, so <see cref="Pilot.CashCents"/> always equals the sum of the pilot's
/// ledger entries (brief §6: "Do not compute balances any other way"). Keeping all
/// movement here also means a future shared-world server (competitive addendum §6) could
/// become authoritative over the ledger without changing callers.
/// </summary>
public sealed class LedgerService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;

    public LedgerService(CallsignDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Post a money movement (positive = credit, negative = debit).</summary>
    public async Task<LedgerEntry> PostAsync(
        Guid pilotId, LedgerCategory category, decimal amount, string description,
        string? relatedEntityId = null, CancellationToken ct = default)
    {
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId, ct)
                    ?? throw new InvalidOperationException($"Pilot {pilotId} not found.");

        var entry = new LedgerEntry
        {
            PilotId = pilotId,
            At = _clock.UtcNow,
            Category = category,
            AmountCents = ToCents(amount),
            Description = description,
            RelatedEntityId = relatedEntityId,
        };

        _db.LedgerEntries.Add(entry);
        pilot.CashCents += entry.AmountCents; // persisted atomically with the entry
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Authoritative balance: the sum of the pilot's ledger entries, in cents.</summary>
    public Task<long> GetBalanceCentsAsync(Guid pilotId, CancellationToken ct = default)
        => _db.LedgerEntries.Where(e => e.PilotId == pilotId).SumAsync(e => e.AmountCents, ct);

    /// <summary>Convert a decimal currency amount to integer cents.</summary>
    internal static long ToCents(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
