using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Callsign.Core.World;
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
    private readonly WorldOracle _world;

    public TradeService(CallsignDbContext db, LedgerService ledger, MarketService market, IClock clock, EconomyConfig cfg, WorldOracle world)
    {
        _db = db;
        _ledger = ledger;
        _market = market;
        _clock = clock;
        _cfg = cfg;
        _world = world;
    }

    /// <summary>The local weather's demand lift for a field (Phase 8f): foul conditions there lift its prices.
    /// Neutral (1.0) when the airport is unknown — so callers/tests without geography price the market unchanged.</summary>
    private async Task<double> WeatherFactorAsync(string icao, DateTimeOffset now, CancellationToken ct)
    {
        var airport = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == icao, ct);
        if (airport is null) return 1.0;
        var w = _world.WeatherAt(airport.Latitude, airport.Longitude, now);
        return _cfg.WeatherDemandFactor(w.VisibilitySm);
    }

    /// <summary>The market at <paramref name="icao"/> priced through <paramref name="companyId"/>'s own
    /// trading footprint here — the same prices its buy/sell will use, so what you see is what you pay.</summary>
    public async Task<IReadOnlyList<MarketQuote>> GetMarketAsync(Guid companyId, string icao, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var rows = await _db.MarketPressures
            .Where(p => p.CompanyId == companyId && p.Icao == icao)
            .ToListAsync(ct);
        long Eff(string good) => rows.FirstOrDefault(r => string.Equals(r.Good, good, StringComparison.OrdinalIgnoreCase)) is { } r
            ? MarketService.DecayPressureLbs(r.PressureLbs, now - r.UpdatedAt, _cfg.MarketPressureHalfLife)
            : 0;
        double weather = await WeatherFactorAsync(icao, now, ct);
        return TradeCatalog.Goods.Select(g => _market.Quote(icao, g, Eff(g.Key), weather)).ToList();
    }

    // The company's own decayed trading footprint on one market cell, plus the live row to fold into.
    private async Task<(MarketPressure? row, long effectiveLbs)> LoadPressureAsync(
        Guid companyId, string icao, string good, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _db.MarketPressures.FirstOrDefaultAsync(
            p => p.CompanyId == companyId && p.Icao == icao && p.Good == good, ct);
        long eff = row is null ? 0 : MarketService.DecayPressureLbs(row.PressureLbs, now - row.UpdatedAt, _cfg.MarketPressureHalfLife);
        return (row, eff);
    }

    // Fold a trade's weight into the local pressure: re-anchor the decayed value at `now` and add the delta.
    // Buying pushes it positive (price up next time), selling negative (price down). Rides the trade's own
    // SaveChanges, so a replayed/raced trade — which returns before reaching here — never double-applies it.
    private void ApplyPressure(MarketPressure? row, Guid companyId, string icao, string good,
        long decayedNowLbs, long deltaLbs, DateTimeOffset now)
    {
        if (row is null)
            _db.MarketPressures.Add(new MarketPressure
            {
                Id = Guid.NewGuid(), CompanyId = companyId, Icao = icao, Good = good,
                PressureLbs = decayedNowLbs + deltaLbs, UpdatedAt = now,
            });
        else
        {
            row.PressureLbs = decayedNowLbs + deltaLbs;
            row.UpdatedAt = now;
        }
    }

    /// <summary>Everything you're holding, valued at the market where it currently sits.</summary>
    public async Task<IReadOnlyList<InventoryView>> GetInventoryAsync(Guid companyId, CancellationToken ct = default)
    {
        var lots = await _db.InventoryLots
            .Where(l => l.CompanyId == companyId && l.Quantity > 0 && !l.IsDeleted)
            .ToListAsync(ct);

        // Value each lot at the sell price it would actually fetch where it sits — including the softening
        // effect of anything you've already dumped there, so unrealised P&L doesn't overstate a flooded market.
        var now = _clock.UtcNow;
        var pressRows = await _db.MarketPressures.Where(p => p.CompanyId == companyId).ToListAsync(ct);
        long Eff(string icao, string good) => pressRows.FirstOrDefault(
            r => r.Icao == icao && string.Equals(r.Good, good, StringComparison.OrdinalIgnoreCase)) is { } r
            ? MarketService.DecayPressureLbs(r.PressureLbs, now - r.UpdatedAt, _cfg.MarketPressureHalfLife)
            : 0;
        // Each lot is valued at the weather-adjusted sell price where it sits — compute the factor once per field.
        var weatherByIcao = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in lots.Select(l => l.LocationIcao).Distinct(StringComparer.OrdinalIgnoreCase))
            weatherByIcao[loc] = await WeatherFactorAsync(loc, now, ct);

        var views = new List<InventoryView>(lots.Count);
        foreach (var l in lots)
        {
            var g = TradeCatalog.Find(l.Good);
            long sell = g is null ? 0 : _market.Quote(l.LocationIcao, g, Eff(l.LocationIcao, l.Good), weatherByIcao.GetValueOrDefault(l.LocationIcao, 1.0)).SellCents;
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

        var now = _clock.UtcNow;
        var (pRow, effPressure) = await LoadPressureAsync(companyId, icao, good.Key, now, ct);
        double weather = await WeatherFactorAsync(icao, now, ct);
        var quote = _market.Quote(icao, good, effPressure, weather);   // priced at your footprint + local weather, before this order
        long cost = quote.BuyCents * (long)qty;
        if (company.CashCents < cost)
            throw new InvalidOperationException(
                $"Not enough cash: {qty} × {good.Name} costs {cost / 100m:C0}, you have {company.Cash:C0}.");

        await EnsureCarryCapacityAsync(companyId, qty * good.UnitWeightLbs, ct);
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

        // Draining local supply lifts the price for your next order here.
        ApplyPressure(pRow, companyId, icao, good.Key, effPressure, +(long)qty * good.UnitWeightLbs, now);

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
    public async Task<TradeResult> SellAsync(
        Guid companyId, string icao, string goodKey, int qty, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (qty <= 0)
            throw new InvalidOperationException("Quantity must be a positive whole number.");
        var good = TradeCatalog.Find(goodKey)
                   ?? throw new InvalidOperationException($"Unknown commodity '{goodKey}'.");

        // A retried sale settles once: replay the realised result from the prior posting instead of
        // crediting (and de-stocking) twice. Proceeds come straight from the ledger; the breakdown is
        // rebuilt from the lot's cost basis (a sale leaves it unchanged) — exact for an immediate retry.
        string? dedupe = idempotencyKey is null ? null : $"trade-sell:{idempotencyKey}";
        async Task<TradeResult> ReplayAsync(LedgerEntry e)
        {
            int soldQty = ParseSoldQty(e.Description);
            long basisPer = e.RelatedEntityId is string s && Guid.TryParse(s, out var lid)
                ? (await _db.InventoryLots.FirstOrDefaultAsync(l => l.Id == lid, ct))?.UnitCostCents ?? 0
                : 0;
            long cost = basisPer * soldQty;
            return new TradeResult(soldQty, e.AmountCents, cost, e.AmountCents - cost);
        }
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } prior)
            return await ReplayAsync(prior);

        var lot = await _db.InventoryLots.FirstOrDefaultAsync(
            l => l.CompanyId == companyId && l.Good == good.Key && l.LocationIcao == icao && !l.IsDeleted, ct)
                  ?? throw new InvalidOperationException($"You have no {good.Name} at {icao} to sell.");
        if (lot.Quantity < qty)
            throw new InvalidOperationException($"You only hold {lot.Quantity} × {good.Name} at {icao}.");

        var now = _clock.UtcNow;
        var (pRow, effPressure) = await LoadPressureAsync(companyId, icao, good.Key, now, ct);
        double weather = await WeatherFactorAsync(icao, now, ct);
        var quote = _market.Quote(icao, good, effPressure, weather);   // priced at your footprint + local weather, before this sale
        long proceeds = quote.SellCents * (long)qty;
        long costBasis = lot.UnitCostCents * (long)qty;
        long pnl = proceeds - costBasis;

        lot.Quantity -= qty;
        lot.UpdatedAt = now;

        // Flooding the local market softens the price for your next sale here.
        ApplyPressure(pRow, companyId, icao, good.Key, effPressure, -(long)qty * good.UnitWeightLbs, now);

        string pnlText = pnl >= 0 ? $"+{pnl / 100m:C0}" : $"-{Math.Abs(pnl) / 100m:C0}";
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new LedgerPosting(LedgerCategory.Trade, proceeds / 100m, $"Sold {qty} × {good.Name} at {icao} (P&L {pnlText})",
                LedgerRefType.StockLot, lot.Id.ToString(), DedupeKey: dedupe),
        }, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            if (await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is { } raced)
                return await ReplayAsync(raced);
            throw;
        }
        return new TradeResult(qty, proceeds, costBasis, pnl);
    }

    // Recover the sold quantity from our own posting description ("Sold {n} × …") for a replayed result.
    private static int ParseSoldQty(string description)
    {
        const string prefix = "Sold ";
        if (!description.StartsWith(prefix, StringComparison.Ordinal))
            return 0;
        int i = prefix.Length, n = 0;
        while (i < description.Length && char.IsDigit(description[i]))
            n = n * 10 + (description[i++] - '0');
        return n;
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
