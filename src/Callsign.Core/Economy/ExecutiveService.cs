using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A hireable executive preview. Salary is economy-set (a deterministic roll off the seed), never
/// player-set — regenerated server-side on hire so it can't be tampered with.</summary>
public sealed record ExecutiveCandidate(int Seed, ExecutiveRole Role, string Name, long SalaryPerDayCents, int CompetenceMilli);

/// <summary>One seat on the org chart — its role, what it runs, whoever holds it (null = vacant), and (when
/// filled) a short readout of the LIVE effect that holder's competence is having on the operation (Phase 16f).</summary>
public sealed record ExecutiveSeat(ExecutiveRole Role, string Title, string Mandate, Executive? Holder, string? Effect);

/// <summary>The whole org at a glance: every seat (filled or vacant), the management strength the filled seats
/// add up to (0–100000), the total daily salary, and how much that strength lifts autonomous ops.</summary>
public sealed record OrgReadout(
    int StrengthMilli, int RolesFilled, int RoleCount, long DailySalaryCents, int OpsSkillBoostMilli,
    IReadOnlyList<ExecutiveSeat> Seats);

/// <summary>
/// The executive suite (Phase 16c) — the spine of the airline endgame ("the org chart is the difficulty dial").
/// Once incorporated you hire a C-suite, one per role; together the filled seats are the org's MANAGEMENT
/// STRENGTH, which lifts how well the autonomous operation runs (see <see cref="OrgSkillBoostMilli"/>, folded
/// into the operating-reputation convergence in <c>OperationsService.ReconcileAsync</c>). Salaries are a real
/// recurring cost. Purely additive over the existing crew/economy — no new wallet or income loop.
/// </summary>
public sealed class ExecutiveService
{
    private static readonly string[] First = ["Rosa", "Idris", "Mei", "Anton", "Farah", "Bjorn", "Nia", "Cyrus", "Hana", "Leon", "Sofia", "Kwame"];
    private static readonly string[] Last = ["Delacroix", "Osei", "Chen", "Berg", "Nasser", "Halvorsen", "Adeyemi", "Rashid", "Takahashi", "Moreau", "Castellano", "Mensah"];

    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public ExecutiveService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    /// <summary>All five seats in order, with a title + one-line mandate each.</summary>
    public static readonly IReadOnlyList<(ExecutiveRole Role, string Title, string Mandate)> Catalog = new[]
    {
        (ExecutiveRole.ChiefOperating,      "Chief Operating Officer", "Runs day-to-day operations — the schedule flies itself."),
        (ExecutiveRole.ChiefFinancial,      "Chief Financial Officer", "Minds the books and keeps the operation solvent."),
        (ExecutiveRole.ChiefPilot,          "Chief Pilot",             "Owns crews and flying standards across the line."),
        (ExecutiveRole.NetworkPlanner,      "Network Planner",         "Shapes the route network and the schedule."),
        (ExecutiveRole.MaintenanceDirector, "Maintenance Director",    "Keeps the fleet airworthy and out of the shop."),
    };

    private static string TitleOf(ExecutiveRole r) => Catalog.First(c => c.Role == r).Title;

    /// <summary>A short, live readout of what a seated executive's competence is doing to the operation right now —
    /// so the org's depth is legible (Phase 16f). Derived from the same config factors the reconcile applies.</summary>
    public static string SeatEffect(EconomyConfig cfg, ExecutiveRole role, int competenceMilli)
    {
        double c = Math.Clamp(competenceMilli, 0, 100_000) / 100_000.0;
        int Pct(double factor) => (int)Math.Round(c * factor * 100);
        return role switch
        {
            ExecutiveRole.ChiefOperating      => $"+{Pct(cfg.CooDutyBonusFactor)}% autonomous throughput",
            ExecutiveRole.ChiefFinancial      => $"-{Pct(cfg.CfoFuelDiscountFactor)}% fuel cost",
            ExecutiveRole.ChiefPilot          => $"crews tire ~{Pct(cfg.ChiefPilotFatigueReliefFactor)}% slower",
            ExecutiveRole.NetworkPlanner      => $"-{Pct(cfg.NetworkPlannerCompetitionDefenseFactor)}% rival pressure",
            ExecutiveRole.MaintenanceDirector => $"-{Pct(cfg.MaintWearReductionFactor)}% airframe wear",
            _ => "",
        };
    }

    // --- Pure org math (also used by ReconcileAsync via the static helpers) ---

    /// <summary>The org's management strength (0–100000): the average competence of the filled seats, scaled by
    /// how many of the roles are covered — so a full, competent C-suite scores high; a lone hire, low.</summary>
    public static int OrgStrengthMilli(IReadOnlyCollection<Executive> active, int roleCount)
    {
        if (active.Count == 0 || roleCount <= 0) return 0;
        // One per role is enforced on hire, but be defensive: fold to distinct roles, best competence per role.
        var perRole = active.GroupBy(e => e.Role).Select(g => g.Max(e => e.CompetenceMilli)).ToList();
        double avg = perRole.Average();
        double coverage = Math.Min(1.0, perRole.Count / (double)roleCount);
        return (int)Math.Round(avg * coverage);
    }

    /// <summary>How much the org's strength lifts the effective crew skill on autonomous legs (milli). Bounded by
    /// the config factor; the operating-rep move it feeds is still step-capped, no-overshoot-clamped and hard-
    /// bounded to 100, so this can only ever help within those rails.</summary>
    public static int OrgSkillBoostMilli(EconomyConfig cfg, int strengthMilli)
        => (int)Math.Round(Math.Max(0, strengthMilli) * cfg.ExecOrgSkillBoostFactor);

    /// <summary>The org's active skill boost for a company (0 when there's no suite). Read once per reconcile pass.</summary>
    public async Task<int> OrgSkillBoostForAsync(Guid companyId, CancellationToken ct = default)
    {
        var active = await _db.Executives.Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted).ToListAsync(ct);
        return OrgSkillBoostMilli(_cfg, OrgStrengthMilli(active, _cfg.ExecutiveRoleCount));
    }

    // --- Read model ---

    public async Task<OrgReadout> GetOrgAsync(Guid companyId, CancellationToken ct = default)
    {
        var active = await _db.Executives.Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted).ToListAsync(ct);
        var byRole = active.GroupBy(e => e.Role).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CompetenceMilli).First());
        var seats = Catalog.Select(c =>
        {
            var holder = byRole.GetValueOrDefault(c.Role);
            return new ExecutiveSeat(c.Role, c.Title, c.Mandate, holder, holder is null ? null : SeatEffect(_cfg, c.Role, holder.CompetenceMilli));
        }).ToList();
        int strength = OrgStrengthMilli(active, _cfg.ExecutiveRoleCount);
        long salary = active.Sum(e => e.SalaryPerDayCents);
        return new OrgReadout(strength, byRole.Count, _cfg.ExecutiveRoleCount, salary, OrgSkillBoostMilli(_cfg, strength), seats);
    }

    // --- Market ---

    private ExecutiveCandidate MakeCandidate(int seed)
    {
        var r = new Random(seed);
        var role = (ExecutiveRole)(1 + r.Next(_cfg.ExecutiveRoleCount));
        int comp = 45_000 + r.Next(50_001);                 // 45%..95% — the C-suite is a cut above line crew
        long salary = _cfg.ExecutiveBaseSalaryCentsPerDay + comp / 1000L * _cfg.ExecutiveSalaryCentsPerCompetencePoint;
        var name = $"{First[r.Next(First.Length)]} {Last[r.Next(Last.Length)]}";
        return new ExecutiveCandidate(seed, role, name, salary, comp);
    }

    /// <summary>A deterministic slate of hireable executives for the roles you HAVEN'T filled yet — a couple of
    /// candidates per open seat, unique names, salary economy-set. Empty once the suite is full.</summary>
    public async Task<IReadOnlyList<ExecutiveCandidate>> GenerateMarketAsync(Guid companyId, int perRole = 2, CancellationToken ct = default)
    {
        var filled = (await _db.Executives.Where(e => e.CompanyId == companyId && e.IsActive && !e.IsDeleted).Select(e => e.Role).ToListAsync(ct)).ToHashSet();
        int companySeed = companyId.GetHashCode();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ExecutiveCandidate>();
        foreach (var (role, _, _) in Catalog)
        {
            if (filled.Contains(role)) continue;
            int made = 0, guard = 0;
            while (made < perRole && guard++ < perRole * 40)
            {
                // Seed off company + role + attempt so the slate is stable per save but a role's candidates differ.
                int seed = unchecked(companySeed * 31 + (int)role * 7919 + guard);
                var c = MakeCandidate(seed) with { Role = role };
                if (seen.Add(c.Name)) { list.Add(c); made++; }
            }
        }
        return list;
    }

    // --- Commands ---

    private async Task<Company> RequireIncorporatedAsync(Guid companyId, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        bool incorporated = company.AirlineIncorporatedAt is not null || !string.IsNullOrWhiteSpace(company.AirlineName);
        if (!incorporated) throw new InvalidOperationException("Found your airline before building a C-suite — executives run an airline, not an operator.");
        return company;
    }

    /// <summary>Hire the candidate identified by its seed (regenerated server-side, so salary + role are trusted).
    /// Gated: you must have incorporated, and the role must be open (one hire per seat).</summary>
    public async Task<Executive> HireAsync(Guid companyId, int candidateSeed, ExecutiveRole role, CancellationToken ct = default)
    {
        await RequireIncorporatedAsync(companyId, ct);
        bool filled = await _db.Executives.AnyAsync(e => e.CompanyId == companyId && e.Role == role && e.IsActive && !e.IsDeleted, ct);
        if (filled) throw new InvalidOperationException($"Your {TitleOf(role)} seat is already filled — let them go first to change it.");

        var c = MakeCandidate(candidateSeed) with { Role = role };
        var now = _clock.UtcNow;
        var exec = new Executive
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Role = role, Name = c.Name,
            CompetenceMilli = c.CompetenceMilli, SalaryPerDayCents = c.SalaryPerDayCents,
            HiredAt = now, LastPaidAt = now, IsActive = true, UpdatedAt = now,
        };
        _db.Executives.Add(exec);
        await _db.SaveChangesAsync(ct);
        return exec;
    }

    /// <summary>Let an executive go: books the salary owed up to now (so the [LastPaidAt, now] segment isn't lost
    /// once the reconcile loop skips them), then soft-dismisses them.</summary>
    public async Task DismissAsync(Guid companyId, Guid execId, CancellationToken ct = default)
    {
        var exec = await _db.Executives.FirstOrDefaultAsync(e => e.Id == execId && e.CompanyId == companyId && e.IsActive && !e.IsDeleted, ct)
                   ?? throw new InvalidOperationException("Executive not found.");
        var now = _clock.UtcNow;
        long owed = (long)Math.Round((now - exec.LastPaidAt).TotalDays * exec.SalaryPerDayCents);
        if (owed > 0)
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.StaffWage, -(owed / 100m), $"Final salary — {exec.Name} ({TitleOf(exec.Role)})",
                    DedupeKey: $"execwage:{exec.Id}:{exec.LastPaidAt.UtcTicks}"),
            }, ct);
        exec.IsActive = false;
        exec.LastPaidAt = now;
        exec.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }
}
