using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>
/// What a money movement was for. Persisted as a STRING so new members and per-phase
/// splits never corrupt existing rows.
/// </summary>
public enum LedgerCategory
{
    StartingBalance,
    JobPayout,
    JobBonus,
    AirportFee,
    Penalty,
    Fuel,            // consumable tank fuel — NOT the tradeable fuel commodity
    Repair,
    AircraftPurchase,
    AircraftRental,
    BaseRent,
    StaffWage,
    LoanPrincipal,
    LoanInterest,
    LoanPayment,
    Trade,
    Transfer,
    Adjustment,
}

/// <summary>
/// Typed discriminator for <see cref="LedgerEntry.RelatedEntityId"/>. Values are pinned —
/// this is persisted, so never renumber.
/// </summary>
public enum LedgerRefType
{
    Job = 1,
    Loan = 2,
    StockLot = 3,
    CheckFlight = 4,
    Campaign = 5,
    InsuranceClaim = 6,
    Rental = 7,
    Fuel = 8,
}

/// <summary>
/// An append-only record of a single CASH movement. The ledger is the single source of
/// truth for cash (brief §6); assets and liabilities are modelled as their own entities.
/// Amounts are integer cents; positive = credit, negative = debit.
/// </summary>
public sealed class LedgerEntry
{
    /// <summary>Local running-balance order key (autoincrement). Meaningful only on this client.</summary>
    public long Id { get; set; }

    /// <summary>Globally-unique idempotent identity for replay/merge.</summary>
    public Guid EntryUid { get; set; }

    /// <summary>Reserved for a server-assigned global order (null locally).</summary>
    public long? Sequence { get; set; }

    /// <summary>Owning account (<see cref="Company"/>).</summary>
    public Guid AccountId { get; set; }

    public DateTimeOffset At { get; set; }
    public LedgerCategory Category { get; set; }
    public long AmountCents { get; set; }
    public string Description { get; set; } = null!;

    // Attribution for per-asset P&L — a single flight payout can roll up to several of these.
    public Guid? AircraftInstanceId { get; set; }
    public Guid? StaffId { get; set; }
    public Guid? BaseId { get; set; }
    public LedgerRefType? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }

    /// <summary>Idempotency key for recurring/offline postings; unique per account.</summary>
    public string? DedupeKey { get; set; }

    [NotMapped]
    public decimal Amount => AmountCents / 100m;
}
