namespace Callsign.Core.Domain;

/// <summary>What a staff member does. Pilots fly your lines; a Manager (Phase 12) runs a base — keeping the
/// owned fleet parked there serviced and airworthy automatically, for a daily wage.</summary>
public enum StaffRole { Pilot = 1, Manager = 2 }

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

    /// <summary>Persistent crew fatigue (Phase 16d), 0..100000. Accrues with duty hours flown on autonomous
    /// lines and recovers over rest; high fatigue cuts the crew's EFFECTIVE flying skill (more diversions, a
    /// slower-growing name), so one crew can't be run round the clock without a cost. A Chief Pilot's rostering
    /// (16c executive) slows the accrual and speeds recovery. Additive; 0 = fully rested (legacy rows read 0).</summary>
    public int FatigueMilli { get; set; }

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
