using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>One itemised line of an aircraft price ("why this costs what it does").</summary>
public sealed record AircraftPriceFactor(string Label, long AmountCents);

/// <summary>A buy price with its itemised factors. <c>Factors</c> sum to <c>TotalCents</c>.</summary>
public sealed record AircraftPriceQuote(long TotalCents, IReadOnlyList<AircraftPriceFactor> Factors);

/// <summary>
/// Buy price for an aircraft type: a category base plus itemised spec premiums (domain-notes §6.2 —
/// "factors shown on hover"). Deterministic and economy-computed; never player-set.
/// </summary>
public static class AircraftPricing
{
    public static AircraftPriceQuote Quote(EconomyConfig cfg, AircraftType t)
    {
        var factors = new List<AircraftPriceFactor>
        {
            new($"Base · {t.Category}", cfg.AircraftBaseCents(t.Category)),
        };
        if (t.UsefulLoadLbs is int load && load > 0)
            factors.Add(new($"Payload · {load:N0} lb", load * cfg.AircraftPricePerUsefulLbCents));
        if (t.Seats is int seats && seats > 0)
            factors.Add(new($"Seats · {seats}", seats * cfg.AircraftPricePerSeatCents));
        if (t.CruiseKtas is int kt && kt > 100)
            factors.Add(new($"Speed · {kt} kt", (kt - 100) * cfg.AircraftPricePerCruiseKtCents));

        return new AircraftPriceQuote(factors.Sum(f => f.AmountCents), factors);
    }
}
