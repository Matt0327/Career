using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>What a money movement was for. Categories drive the P&amp;L views (brief §3.3).</summary>
public enum LedgerCategory
{
    StartingBalance,
    JobPayout,
    Penalty,
    Fuel,
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
/// An append-only record of a single money movement. The ledger is the single source of
/// truth for all balances and P&amp;L (brief §6). Amounts are integer cents; positive is a
/// credit, negative is a debit.
/// </summary>
public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid PilotId { get; set; }
    public DateTimeOffset At { get; set; }
    public LedgerCategory Category { get; set; }
    public long AmountCents { get; set; }
    public string Description { get; set; } = null!;
    public string? RelatedEntityId { get; set; }

    [NotMapped]
    public decimal Amount => AmountCents / 100m;
}
