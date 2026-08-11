namespace Callsign.Core.Economy;

/// <summary>
/// The near-first destination bias shared by every local job source, defined once so cargo and
/// passenger boards lean the same way. Consumes exactly one random draw per call, keeping each
/// source's sequence reproducible for a given seed.
/// </summary>
internal static class JobRng
{
    /// <summary>
    /// Draw an index into a nearest-first list, biased toward the front so short regional hops appear
    /// reliably. A uniform draw over a wide radius is area-dominated (far airports vastly outnumber
    /// near ones), so without this the board is almost all long hauls. The tail still reaches the
    /// occasional long haul; <paramref name="biasExponent"/> (&gt;1) controls the lean.
    /// </summary>
    public static int NearBiasedIndex(Random rng, int count, double biasExponent)
        => Math.Min((int)(Math.Pow(rng.NextDouble(), biasExponent) * count), count - 1);
}
