using Callsign.Core.Economy;

namespace Callsign.Core.Airline;

/// <summary>
/// "The Flotation" (Phase 13): reads the airline's enterprise value as an investor SHARE PRICE. It is purely a
/// re-framing of <see cref="AirlineValuation"/> — market cap equals the enterprise value, divided across a fixed
/// notional share count — so no new money is minted and it gates nothing. The <see cref="Readout.History"/> series
/// (built from periodic value snapshots) is the share-price ticker; the earliest mark is the flotation baseline,
/// against which "growth since flotation" is measured. Pure and deterministic, a read model like net worth.
/// </summary>
public static class AirlineMarket
{
    public sealed record Point(DateTimeOffset AtUtc, long SharePriceCents);
    public sealed record Readout(
        long SharePriceCents, long MarketCapCents, long SharesOutstanding,
        long FlotationSharePriceCents, double GrowthSinceFlotationPct, IReadOnlyList<Point> History);

    /// <summary>Enterprise value split across the shares, rounded to whole cents (0 if no shares).</summary>
    public static long SharePriceCents(long valuationCents, long shares)
        => shares > 0 ? (long)Math.Round((double)valuationCents / shares) : 0;

    /// <param name="history">Value marks oldest-first: (when, enterprise value at that mark).</param>
    public static Readout Compute(EconomyConfig cfg, long valuationCents, IReadOnlyList<(DateTimeOffset At, long Val)> history)
    {
        long shares = Math.Max(1, cfg.AirlineSharesOutstanding);
        long price = SharePriceCents(valuationCents, shares);
        // The flotation baseline is the earliest mark on record; before any mark exists, "now" is the baseline
        // (0% growth), so a just-incorporated airline reads flat rather than undefined.
        long flotationVal = history.Count > 0 ? history[0].Val : valuationCents;
        double growth = flotationVal > 0 ? (double)(valuationCents - flotationVal) / flotationVal : 0;
        var points = history.Select(h => new Point(h.At, SharePriceCents(h.Val, shares))).ToList();
        return new Readout(price, valuationCents, shares, SharePriceCents(flotationVal, shares), growth, points);
    }
}
