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
    /// <summary>Hand-set sticker prices for halo/flagship aircraft, keyed by <see cref="AircraftType.Key"/>.
    /// These override the spec-derived quote entirely — a flagship is priced by desirability, not by payload.
    /// Cents.</summary>
    public static readonly IReadOnlyDictionary<string, long> HaloPrices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["CONC"] = 50_000_000_000, // Concorde — $500,000,000
    };

    /// <summary>True if this type has a hand-set halo price (buy-only flagship — never rented).</summary>
    public static bool IsHalo(string? key) => key is not null && HaloPrices.ContainsKey(key);

    public static AircraftPriceQuote Quote(EconomyConfig cfg, AircraftType t)
    {
        if (t.Key is { } k && HaloPrices.TryGetValue(k, out var halo))
            return new AircraftPriceQuote(halo, [new AircraftPriceFactor($"Flagship · {t.CanonicalName}", halo)]);

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
