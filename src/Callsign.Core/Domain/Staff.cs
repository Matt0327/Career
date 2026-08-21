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

    /// <summary>
    /// Where this pilot currently is (Phase 12 crew-location realism). A hired pilot lives at a field: they
    /// are based where you recruit them, must be co-located with an aircraft to crew its line, and you pay to
    /// reposition (deadhead) them elsewhere. Null = un-positioned (legacy rows before this field existed, and
    /// any pilot never placed) → treated as co-located with anything, so it never blocks an existing save.
    /// </summary>
    public string? CurrentIcao { get; set; }

    public DateTimeOffset HiredAt { get; set; }
    public DateTimeOffset LastPaidAt { get; set; }   // wage-accrual watermark
    public bool IsActive { get; set; } = true;

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
