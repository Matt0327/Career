namespace Callsign.Core.Domain;

/// <summary>Where a loan is in its life. Pinned (persisted as a string).</summary>
public enum LoanStatus { Active = 1, PaidOff = 2, Defaulted = 3 }

/// <summary>
/// A loan (Phase 4a): a liability tracked SEPARATELY from cash — the ledger records the cash that moves
/// (draw-down, interest, repayments), while the outstanding principal lives here. Interest accrues on the
/// declining balance and is billed, with a straight-line principal repayment, in the offline reconcile
/// pass. Syncable for the future shared world.
/// </summary>
public sealed class Loan : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public int Tier { get; set; }
    public long PrincipalCents { get; set; }        // original amount borrowed
    public int AprBps { get; set; }                 // annual rate in basis points, snapshot at draw-down
    public int TermDays { get; set; }               // repayment horizon
    public long OutstandingCents { get; set; }      // principal still owed
    public DateTimeOffset TakenAt { get; set; }
    public DateTimeOffset PaymentLastBilledAt { get; set; } // reconcile watermark
    public LoanStatus Status { get; set; } = LoanStatus.Active;

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
