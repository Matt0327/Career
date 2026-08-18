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

/// <summary>One attributed subject's flows over the window (aircraft tail / pilot / base).</summary>
public sealed record AttributionLine(
    string Kind, string Id, string Label, string Sub,
    long IncomeCents, long ExpenseCents, long NetCents, int Entries);
public sealed record AttributionPnl(
    IReadOnlyList<AttributionLine> Aircraft, IReadOnlyList<AttributionLine> Staff, IReadOnlyList<AttributionLine> Bases);
public sealed record CashPoint(DateTimeOffset At, long IncomeCents, long ExpenseCents, long NetCents, long CashCents);
public sealed record CashFlowSeries(int Days, int Buckets, IReadOnlyList<CashPoint> Points);
public sealed record StatementRow(
    DateTimeOffset At, string Category, long AmountCents, string Description,
    string? Aircraft, string? Staff, string? Base);

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

    /// <summary>
    /// Per-asset / per-employee / per-base P&L over the window — the roll-up of the ledger's attribution
    /// columns (AircraftInstanceId, StaffId, BaseId) that nothing has surfaced until now.
    /// </summary>
    public async Task<AttributionPnl> AttributionProfitLossAsync(Guid companyId, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var since = _clock.UtcNow.AddDays(-days);
        var entries = await _db.LedgerEntries
            .Where(e => e.AccountId == companyId && e.At >= since)
            .ToListAsync(ct);

        var acIds = entries.Where(e => e.AircraftInstanceId != null).Select(e => e.AircraftInstanceId!.Value).Distinct().ToList();
        var instances = await _db.AircraftInstances.Where(a => acIds.Contains(a.Id)).ToListAsync(ct);
        var typeIds = instances.Select(a => a.TypeId).Distinct().ToList();
        var typeNames = await _db.AircraftTypes.Where(t => typeIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.CanonicalName, ct);
        var acMap = instances.ToDictionary(a => a.Id, a => (a.Tail, Name: typeNames.GetValueOrDefault(a.TypeId, "Aircraft")));

        var stIds = entries.Where(e => e.StaffId != null).Select(e => e.StaffId!.Value).Distinct().ToList();
        var staffNames = await _db.Staff.Where(s => stIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var bIds = entries.Where(e => e.BaseId != null).Select(e => e.BaseId!.Value).Distinct().ToList();
        var baseIcaos = await _db.Bases.Where(b => bIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.AirportIcao, ct);

        static List<AttributionLine> Roll<TKey>(
            IEnumerable<LedgerEntry> src, Func<LedgerEntry, TKey?> key, string kind,
            Func<TKey, (string Label, string Sub)> resolve) where TKey : struct
        {
            return src.Where(e => key(e) != null)
                .GroupBy(e => key(e)!.Value)
                .Select(g =>
                {
                    long inc = g.Where(e => e.AmountCents > 0).Sum(e => e.AmountCents);
                    long exp = g.Where(e => e.AmountCents < 0).Sum(e => e.AmountCents);
                    var (label, sub) = resolve(g.Key);
                    return new AttributionLine(kind, g.Key.ToString()!, label, sub, inc, exp, inc + exp, g.Count());
                })
                .OrderBy(l => l.NetCents)
                .ToList();
        }

        var aircraft = Roll(entries, e => e.AircraftInstanceId, "Aircraft",
            id => acMap.TryGetValue(id, out var v) ? (v.Tail, v.Name) : ("(sold)", "Aircraft"));
        var staff = Roll(entries, e => e.StaffId, "Staff",
            id => (staffNames.GetValueOrDefault(id, "(former staff)"), "Pilot"));
        var bases = Roll(entries, e => e.BaseId, "Base",
            id => (baseIcaos.GetValueOrDefault(id, "(closed)"), "Base"));

        return new AttributionPnl(aircraft, staff, bases);
    }

    /// <summary>A reconstructed cash-balance curve + per-bucket in/out over the window (exact: the ledger is truth).</summary>
    public async Task<CashFlowSeries> CashFlowSeriesAsync(Guid companyId, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var now = _clock.UtcNow;
        var since = now.AddDays(-days);

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");

        var entries = await _db.LedgerEntries
            .Where(e => e.AccountId == companyId && e.At >= since)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToListAsync(ct);

        long windowNet = entries.Sum(e => e.AmountCents);
        long openingCash = company.CashCents - windowNet;

        int buckets = Math.Clamp(Math.Min(days, 30), 1, 60);
        long bucketTicks = (now - since).Ticks / buckets;
        var inc = new long[buckets];
        var exp = new long[buckets];
        foreach (var e in entries)
        {
            int b = bucketTicks <= 0 ? buckets - 1
                  : (int)Math.Clamp((e.At - since).Ticks / bucketTicks, 0, buckets - 1);
            if (e.AmountCents >= 0) inc[b] += e.AmountCents; else exp[b] += e.AmountCents;
        }

        long running = openingCash;
        var points = new List<CashPoint>(buckets);
        for (int b = 0; b < buckets; b++)
        {
            long net = inc[b] + exp[b];
            running += net;
            points.Add(new CashPoint(since.AddTicks(bucketTicks * (b + 1)), inc[b], exp[b], net, running));
        }
        return new CashFlowSeries(days, buckets, points);
    }

    /// <summary>Full itemised statement for the window with attribution resolved — the export/CSV source.</summary>
    public async Task<IReadOnlyList<StatementRow>> StatementAsync(Guid companyId, int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var since = _clock.UtcNow.AddDays(-days);
        var entries = await _db.LedgerEntries
            .Where(e => e.AccountId == companyId && e.At >= since)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .ToListAsync(ct);

        var acIds = entries.Where(e => e.AircraftInstanceId != null).Select(e => e.AircraftInstanceId!.Value).Distinct().ToList();
        var acMap = await _db.AircraftInstances.Where(a => acIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Tail, ct);
        var stIds = entries.Where(e => e.StaffId != null).Select(e => e.StaffId!.Value).Distinct().ToList();
        var stMap = await _db.Staff.Where(s => stIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var bIds = entries.Where(e => e.BaseId != null).Select(e => e.BaseId!.Value).Distinct().ToList();
        var bMap = await _db.Bases.Where(b => bIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.AirportIcao, ct);

        return entries.Select(e => new StatementRow(
            e.At, e.Category.ToString(), e.AmountCents, e.Description,
            e.AircraftInstanceId is { } ai ? acMap.GetValueOrDefault(ai) : null,
            e.StaffId is { } si ? stMap.GetValueOrDefault(si) : null,
            e.BaseId is { } bi ? bMap.GetValueOrDefault(bi) : null)).ToList();
    }
}
