using Callsign.Core.Economy;

namespace Callsign.Core.Airline;

/// <summary>
/// What the airline is WORTH as a going concern (Phase 13) — not just its net assets, but the enterprise value a
/// buyer would pay: the fleet and cash on the books, plus the goodwill of a running airline — its brand
/// (operating reputation), its route network, and the earnings it has demonstrated. Pure and deterministic, a
/// read model like net worth. Informational (it gates nothing); it gives the airline a corporate headline that
/// rewards building a real operation, not just hoarding cash.
/// </summary>
public static class AirlineValuation
{
    public sealed record Line(string Label, long Cents);
    public sealed record Result(long TotalCents, IReadOnlyList<Line> Breakdown);

    public static Result Compute(EconomyConfig cfg, long netWorthCents, int operatingRepMilli, int routes, int scheduled, long lifetimeEarningsCents)
    {
        long brand = (long)Math.Round(operatingRepMilli / 1000.0 * cfg.AirlineRepValuationCents);          // reputation → brand value
        long network = (long)routes * cfg.AirlineRouteValuationCents + (long)scheduled * cfg.AirlineScheduledValuationCents;
        long earnings = (long)Math.Round(Math.Max(0, lifetimeEarningsCents) * cfg.AirlineEarningsValuationFactor); // a going concern trades on the money it makes

        var lines = new List<Line>
        {
            new("Net assets", netWorthCents),
            new("Brand — reputation", brand),
            new("Route network", network),
            new("Earnings goodwill", earnings),
        };
        long total = lines.Sum(l => l.Cents);
        return new Result(total, lines.Where(l => l.Cents != 0).ToList());
    }
}
