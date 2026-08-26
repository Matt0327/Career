namespace Callsign.Core.Domain;

/// <summary>The C-suite roles that run an incorporated airline (Phase 16c). Each is a department the operation
/// delegates to; together the filled seats form the org's "management strength" — how well the airline runs
/// itself. Values are stable (persisted by name); new roles append.</summary>
public enum ExecutiveRole
{
    ChiefOperating = 1,      // COO — runs day-to-day operations
    ChiefFinancial = 2,      // CFO — the books
    ChiefPilot = 3,          // crews & standards
    NetworkPlanner = 4,      // routes & schedule
    MaintenanceDirector = 5, // fleet airworthiness
}

/// <summary>
/// A hired executive (Phase 16c) — a senior, company-level hire, distinct from line crew (<see cref="Staff"/>):
/// they don't fly and aren't stationed at a field. At most one per <see cref="ExecutiveRole"/>. Each has a
/// competence and a daily salary (a recurring ledger debit, like a wage); together the suite's management
/// strength lifts how well the autonomous operation runs. Additive; empty on a save with no C-suite.
/// </summary>
public sealed class Executive : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public ExecutiveRole Role { get; set; }
    public string Name { get; set; } = null!;
    public int CompetenceMilli { get; set; } = 50_000;   // 0..100000
    public long SalaryPerDayCents { get; set; }

    public DateTimeOffset HiredAt { get; set; }
    public DateTimeOffset LastPaidAt { get; set; }        // salary-accrual watermark
    public bool IsActive { get; set; } = true;

    // Sync hooks (dormant until the shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
