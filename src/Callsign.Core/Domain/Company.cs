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

    /// <summary>Which MSFS 2024 edition the player owns (Phase 12 onboarding): "Standard" / "Premium" /
    /// "Deluxe". A profile badge only — it records what they fly with and never gates content or money.
    /// Null on careers created before the choice existed.</summary>
    public string? MsfsEdition { get; set; }

    // --- Airline identity (Phase 5c). Null until the player brands their company; the UI falls back to
    // Name and a derived look. TailCode is a 2–3 letter operator code (e.g. "SBX"); EmblemKey names one of
    // a fixed set of original marks the UI draws. ---
    public string? AirlineName { get; set; }
    public string? TailCode { get; set; }
    public string? AccentColorHex { get; set; }
    public string? EmblemKey { get; set; }

    /// <summary>
    /// The airline's OPERATING reputation (Phase 11a), in thousandths (0–100000 = 0.0–100.0) so tiny
    /// per-leg moves don't round away — the exact scale of <see cref="Pilot.ReputationMilli"/>, but a
    /// SEPARATE figure: the name of the operation you run (your flying + your crew's flying), never the
    /// pilot's personal airmanship. A fresh company starts at 0 — an unearned reputation. Moved only by
    /// <c>SettlementService</c> (player legs, by score) and <c>OperationsService.ReconcileAsync</c>
    /// (autonomous legs, toward crew competence); logged to <see cref="AirlineReputationEvent"/>.
    /// </summary>
    public int OperatingReputationMilli { get; set; }

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
