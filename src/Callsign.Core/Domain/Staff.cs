namespace Callsign.Core.Domain;

/// <summary>What a staff member does. Only pilots in 2d; crew roles come later.</summary>
public enum StaffRole { Pilot = 1 }

/// <summary>
/// A hired employee (Phase 2d). Pilots fly <see cref="StandingOrder"/>s autonomously while you're away.
/// Staff never own cash — the company does; a wage is a recurring ledger debit (domain-notes §4.8).
/// </summary>
public sealed class Staff : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = null!;
    public StaffRole Role { get; set; } = StaffRole.Pilot;
    public long WagePerDayCents { get; set; }
    public int SkillMilli { get; set; } = 50_000;   // 0..100000
    public DateTimeOffset HiredAt { get; set; }
    public DateTimeOffset LastPaidAt { get; set; }   // wage-accrual watermark
    public bool IsActive { get; set; } = true;

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
