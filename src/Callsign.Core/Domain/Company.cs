using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>
/// The account root that owns all money — and, later, aircraft, staff, bases, and loans.
/// Seeded as exactly one row in Phase 1 and presented to the player as "you"; staff pilots
/// never own cash, and a Phase-5 airline slots in here without re-parenting the append-only
/// ledger.
/// <para><see cref="CashCents"/> is a cached balance mutated ONLY by the ledger
/// (see <c>LedgerService</c>); it always equals the sum of this account's ledger entries.</para>
/// </summary>
public sealed class Company : ISyncable
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Cached balance in integer cents. Mutated only via <see cref="ApplyCashDelta"/>.</summary>
    public long CashCents { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token, bumped on every cash move (configured <c>IsConcurrencyToken</c>).
    /// Two money movements that race on the cached balance conflict instead of one silently clobbering
    /// the other — which would desync <see cref="CashCents"/> from the ledger sum (domain-notes §4.1).
    /// </summary>
    public int Version { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }

    [NotMapped]
    public decimal Cash => CashCents / 100m;

    /// <summary>Adjust the cached balance. Internal so only the ledger can move money.</summary>
    internal void ApplyCashDelta(long deltaCents)
    {
        CashCents += deltaCents;
        Version++;
    }
}
