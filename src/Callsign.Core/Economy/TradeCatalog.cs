namespace Callsign.Core.Economy;

/// <summary>A tradeable commodity: a market base price per unit and how much a unit weighs.</summary>
public sealed record TradeGood(string Key, string Name, long BasePriceCents, int UnitWeightLbs);

/// <summary>
/// The commodities you can trade (Phase 2g) — an original, freely-retunable list. Base prices are the
/// mid-market reference; <see cref="MarketService"/> swings them per airport and time window so the
/// same good is cheaper in one place than another, which is where the profit (and the risk) lives.
/// </summary>
public static class TradeCatalog
{
    public static readonly IReadOnlyList<TradeGood> Goods =
    [
        new("produce",     "Fresh produce", 3_200,   50),
        new("textiles",    "Textiles",      6_500,   40),
        new("coffee",      "Coffee",        9_000,   60),
        new("livestock",   "Livestock",    15_000,   90),
        new("spirits",     "Spirits",      28_000,   30),
        new("electronics", "Electronics",  42_000,   20),
        new("machinery",   "Machinery",    68_000,  120),
        new("medicine",    "Medicine",    120_000,   10),
    ];

    public static TradeGood? Find(string key)
    {
        foreach (var g in Goods)
            if (string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }
}
