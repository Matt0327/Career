using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A held commodity lot valued at the current local market, with unrealised P&amp;L.</summary>
public sealed record InventoryView(
    Guid Id, string Good, string Name, int Quantity, long UnitCostCents,
    long MarketSellCents, long UnrealizedPnlCents, int UnitWeightLbs, string LocationIcao);

/// <summary>What a sale realised.</summary>
public sealed record TradeResult(int Quantity, long ProceedsCents, long CostBasisCents, long PnlCents);

/// <summary>
/// Buying and selling commodities (Phase 2g). You buy at your current airport's market, the goods
/// become an <see cref="InventoryLot"/> carrying their cost basis, they ride with you as you fly, and
/// you sell wherever the price is better. Every movement of cash is a ledger posting (the one money
/// rule); a purchase is capped by your fleet's carry capacity so trade scales with the aircraft you own.
/// </summary>
public sealed class TradeService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly MarketService _market;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public TradeService(CallsignDbContext db, LedgerService ledger, MarketService market, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _market = market;
        _clock = clock;
        _cfg = cfg;
    }

    public IReadOnlyList<MarketQuote> Market(string icao) => _market.Quotes(icao);

    /// <summary>Everything you're holding, valued at the market where it currently sits.</summary>
    public async Task<IReadOnlyList<InventoryView>> GetInventoryAsync(Guid companyId, CancellationToken ct = default)
    {
        var lots = await _db.InventoryLots
            .Where(l => l.CompanyId == companyId && l.Quantity > 0 && !l.IsDeleted)
            .ToListAsync(ct);

        var views = new List<InventoryView>(lots.Count);
        foreach (var l in lots)
        {
            var g = TradeCatalog.Find(l.Good);
            long sell = g is null ? 0 : _market.Quote(l.LocationIcao, g).SellCents;
            long unrealized = (sell - l.UnitCostCents) * l.Quantity;
            views.Add(new InventoryView(l.Id, l.Good, g?.Name ?? l.Good, l.Quantity, l.UnitCostCents,
                sell, unrealized, g?.UnitWeightLbs ?? 0, l.LocationIcao));
        }
        return views
            .OrderBy(v => v.LocationIcao)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Buy <paramref name="qty"/> units of a commodity at <paramref name="icao"/>'s market price. Pass a
    /// stable <paramref name="idempotencyKey"/> so a retried request replays instead of buying twice.
    /// </summary>
    public async Task<InventoryLot> BuyAsync(
        Guid companyId, string icao, string goodKey, int qty, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new InvalidOperationException("Quantity must be a positive whole number.");
        var good = TradeCatalog.Find(goodKey)
                   ?? throw new InvalidOperationException($"Unknown commodity '{goodKey}'.");

        string? dedupe = idempotencyKey is null ? null : $"trade-buy:{idempotencyKey}";
        async Task<InventoryLot?> PriorAsync()
        {
            if (dedupe is null) return null;
            var e = await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct);
            return e?.RelatedEntityId is string s && Guid.TryParse(s, out var id)
                ? await _db.InventoryLots.FirstOrDefaultAsync(l => l.Id == id, ct)
                : null;
        }
        if (await PriorAsync() is { } replay) return replay;

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");

        var quote = _market.Quote(icao, good);
        long cost = quote.BuyCents * (long)qty;
        if (company.CashCents < cost)
            throw new InvalidOperationException(
                $"Not enough cash: {qty} × {good.Name} costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        await EnsureCarryCapacityAsync(companyId, qty * good.UnitWeightLbs, ct);

        var now = _clock.UtcNow;
        // One lot per (good, location): merge with a weighted-average cost basis so P&L stays honest.
        var lot = await _db.InventoryLots.FirstOrDefaultAsync(
            l => l.CompanyId == companyId && l.Good == good.Key && l.LocationIcao == icao && !l.IsDeleted, ct);
        if (lot is null)
        {
            lot = new InventoryLot
            {
                Id = Guid.NewGuid(), CompanyId = companyId, Good = good.Key, Quantity = qty,
                UnitCostCents = quote.BuyCents, LocationIcao = icao, AcquiredAt = now, UpdatedAt = now,
            };
            _db.InventoryLots.Add(lot);
        }
        else
        {
            long blended = lot.UnitCostCents * lot.Quantity + quote.BuyCents * (long)qty;
            lot.Quantity += qty;
            lot.UnitCostCents = blended / lot.Quantity; // weighted average (integer cents)
            lot.UpdatedAt = now;
        }

        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Trade, -(cost / 100m), $"Bought {qty} × {good.Name} at {icao}",
                LedgerRefType.StockLot, lot.Id.ToString(), DedupeKey: dedupe),
        }, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await PriorAsync() is { } raced) return raced;
            throw;
        }
        return lot;
    }

    /// <summary>Sell <paramref name="qty"/> units of a commodity you hold at <paramref name="icao"/>.</summary>
    public async Task<TradeResult> SellAsync(Guid companyId, string icao, string goodKey, int qty, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new InvalidOperationException("Quantity must be a positive whole number.");
        var good = TradeCatalog.Find(goodKey)
                   ?? throw new InvalidOperationException($"Unknown commodity '{goodKey}'.");
        var lot = await _db.InventoryLots.FirstOrDefaultAsync(
            l => l.CompanyId == companyId && l.Good == good.Key && l.LocationIcao == icao && !l.IsDeleted, ct)
                  ?? throw new InvalidOperationException($"You have no {good.Name} at {icao} to sell.");
        if (lot.Quantity < qty)
            throw new InvalidOperationException($"You only hold {lot.Quantity} × {good.Name} at {icao}.");

        var quote = _market.Quote(icao, good);
        long proceeds = quote.SellCents * (long)qty;
        long costBasis = lot.UnitCostCents * (long)qty;
        long pnl = proceeds - costBasis;

        lot.Quantity -= qty;
        lot.UpdatedAt = _clock.UtcNow;

        string pnlText = pnl >= 0 ? $"+{pnl / 100m:C0}" : $"-{Math.Abs(pnl) / 100m:C0}";
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Trade, proceeds / 100m, $"Sold {qty} × {good.Name} at {icao} (P&L {pnlText})",
                LedgerRefType.StockLot, lot.Id.ToString()),
        }, ct);
        await _db.SaveChangesAsync(ct);
        return new TradeResult(qty, proceeds, costBasis, pnl);
    }

    /// <summary>The most a company can carry: its best owned airframe's useful load (or a fallback).</summary>
    private async Task<int> CarryCapacityLbsAsync(Guid companyId, CancellationToken ct)
    {
        var typeIds = await _db.AircraftInstances
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .Select(a => a.TypeId)
            .Distinct()
            .ToListAsync(ct);
        int best = 0;
        if (typeIds.Count > 0)
        {
            best = await _db.AircraftTypes
                .Where(t => typeIds.Contains(t.Id) && t.UsefulLoadLbs != null)
                .Select(t => t.UsefulLoadLbs!.Value)
                .OrderByDescending(v => v)
                .FirstOrDefaultAsync(ct);
        }
        return Math.Max(best, _cfg.TradeDefaultHoldLbs);
    }

    private async Task EnsureCarryCapacityAsync(Guid companyId, int addingLbs, CancellationToken ct)
    {
        var lots = await _db.InventoryLots
            .Where(l => l.CompanyId == companyId && l.Quantity > 0 && !l.IsDeleted)
            .ToListAsync(ct);
        long held = lots.Sum(l => (long)(TradeCatalog.Find(l.Good)?.UnitWeightLbs ?? 0) * l.Quantity);
        int cap = await CarryCapacityLbsAsync(companyId, ct);
        if (held + addingLbs > cap)
            throw new InvalidOperationException(
                $"Over carry capacity: holding {held:N0} lb + {addingLbs:N0} lb exceeds {cap:N0} lb (your largest aircraft's useful load).");
    }
}
