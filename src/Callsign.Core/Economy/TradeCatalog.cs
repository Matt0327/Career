namespace Callsign.Core.Economy;

/// <summary>A tradeable commodity: a market base price per unit, how much a unit weighs, and — for perishables
/// — how many days it keeps before it spoils (null = non-perishable, keeps forever).</summary>
public sealed record TradeGood(string Key, string Name, long BasePriceCents, int UnitWeightLbs, int? ShelfLifeDays = null);

/// <summary>
/// The commodities you can trade (Phase 2g) — an original, freely-retunable list. Base prices are the
/// mid-market reference; <see cref="MarketService"/> swings them per airport and time window so the
/// same good is cheaper in one place than another, which is where the profit (and the risk) lives.
/// </summary>
public static class TradeCatalog
{
    public static readonly IReadOnlyList<TradeGood> Goods =
    [
        new("produce",     "Fresh produce", 3_200,   50, ShelfLifeDays: 5),   // perishable — sell it fast
        new("textiles",    "Textiles",      6_500,   40),
        new("coffee",      "Coffee",        9_000,   60),
        new("livestock",   "Livestock",    15_000,   90, ShelfLifeDays: 8),   // live cargo — time-critical
        new("spirits",     "Spirits",      28_000,   30),
        new("electronics", "Electronics",  42_000,   20),
        new("machinery",   "Machinery",    68_000,  120),
        new("medicine",    "Medicine",    120_000,   10, ShelfLifeDays: 21),  // dated stock
    ];

    public static TradeGood? Find(string key)
    {
        foreach (var g in Goods)
            if (string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }
}
