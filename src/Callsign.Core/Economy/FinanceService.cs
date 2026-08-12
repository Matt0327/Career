using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>Net worth as a snapshot: cash + assets − liabilities. Computed, never stored.</summary>
public sealed record NetWorthBreakdown(long CashCents, long AircraftCents, long InventoryCents, long LoansCents, long NetWorthCents);

/// <summary>One category's flows over the window (expense is negative).</summary>
public sealed record PnlLine(string Category, long IncomeCents, long ExpenseCents, long NetCents);

/// <summary>Cash-flow / P&amp;L over a window, by ledger category.</summary>
public sealed record PnlSummary(int Days, long IncomeCents, long ExpenseCents, long NetCents, IReadOnlyList<PnlLine> Lines);

/// <summary>
/// The balance sheet (Phase 4b): a <b>computed</b> view, never a stored number. Net worth is cash (the
/// ledger sum, cached on the company) + assets (each airframe's condition-adjusted resale value, inventory
/// at cost) − liabilities (outstanding loan principal). The P&amp;L view aggregates the ledger by category
/// over a window. No money moves here — it's the lens that makes the rest legible.
/// </summary>
public sealed class FinanceService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public FinanceService(CallsignDbContext db, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
    }

    public async Task<NetWorthBreakdown> NetWorthAsync(Guid companyId, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        long cash = company.CashCents; // authoritative = the ledger sum

        var instances = await _db.AircraftInstances.Where(a => a.CompanyId == companyId && !a.IsDeleted).ToListAsync(ct);
        var typeIds = instances.Select(a => a.TypeId).Distinct().ToList();
        var types = await _db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        long aircraft = 0;
        foreach (var a in instances)
        {
            if (!types.TryGetValue(a.TypeId, out var type))
                continue;
            long market = AircraftPricing.Quote(_cfg, type).TotalCents;
            double condition = Math.Min(a.HullConditionMilli, a.EngineConditionMilli) / 100_000.0;
            aircraft += (long)Math.Round(market * _cfg.AircraftResaleFactor * condition);
        }

        var lots = await _db.InventoryLots.Where(l => l.CompanyId == companyId && l.Quantity > 0 && !l.IsDeleted).ToListAsync(ct);
        long inventory = lots.Sum(l => l.UnitCostCents * (long)l.Quantity);

        long loans = await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted)
            .SumAsync(l => l.OutstandingCents, ct);

        return new NetWorthBreakdown(cash, aircraft, inventory, loans, cash + aircraft + inventory - loans);
    }

    public async Task<PnlSummary> ProfitLossAsync(Guid companyId, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var since = _clock.UtcNow.AddDays(-days);
        var entries = await _db.LedgerEntries.Where(e => e.AccountId == companyId && e.At >= since).ToListAsync(ct);

        var lines = entries
            .GroupBy(e => e.Category)
            .Select(g =>
            {
                long income = g.Where(e => e.AmountCents > 0).Sum(e => e.AmountCents);
                long expense = g.Where(e => e.AmountCents < 0).Sum(e => e.AmountCents);
                return new PnlLine(g.Key.ToString(), income, expense, income + expense);
            })
            .OrderBy(l => l.NetCents)
            .ToList();

        long totalIncome = entries.Where(e => e.AmountCents > 0).Sum(e => e.AmountCents);
        long totalExpense = entries.Where(e => e.AmountCents < 0).Sum(e => e.AmountCents);
        return new PnlSummary(days, totalIncome, totalExpense, totalIncome + totalExpense, lines);
    }
}
