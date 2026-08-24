using Callsign.Core.Airports;
using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// Where a for-sale airframe physically lives. A market aircraft isn't "anywhere you want" — like the real world,
/// it sits at a specific field, and buying it takes delivery THERE (you then fly it home or pay to ferry it). This
/// is the one place that decides which field, so the market listing and the purchase agree byte-for-byte: it's a
/// pure function of the aircraft's seed key and a home-anchored pool of real airfields, so the same aircraft always
/// shows — and delivers — at the same place, and a heavy naturally lands at a real airliner airport (adequate
/// runway) rather than a grass strip it could never leave.
/// </summary>
public static class DealerLocations
{
    /// <summary>How many of the nearest suitable fields an airframe can be placed among. Keeps the dealer network
    /// regional — a hop or two from home, never a cross-country trek — while still spreading stock across fields.</summary>
    public const int PoolSize = 20;

    /// <summary>
    /// Deterministically place a for-sale airframe at a real field. <paramref name="pool"/> is the home-anchored set
    /// of candidate airports, nearest-first (as <see cref="AirportRepository.WithinRadiusAsync"/> returns them). The
    /// field must have a runway adequate for <paramref name="minRunwayFt"/> so you can actually fly what you buy out.
    /// Falls back to <paramref name="fallbackIcao"/> (the buyer's own field, i.e. the old "delivered to you" behaviour)
    /// when the world data has nothing suitable — so an empty airport table degrades gracefully instead of throwing.
    /// </summary>
    public static string Place(string seedKey, int? minRunwayFt, IReadOnlyList<Airport> pool, string fallbackIcao)
    {
        var suitable = new List<Airport>(PoolSize);
        foreach (var a in pool)
        {
            if (!AirportSuitability.IsSuitable(a, minRunwayFt)) continue;
            suitable.Add(a);
            if (suitable.Count == PoolSize) break;
        }
        if (suitable.Count == 0) return fallbackIcao;
        uint h = Fnv1a(seedKey);
        return suitable[(int)(h % (uint)suitable.Count)].Ident;
    }

    // FNV-1a: a stable, framework-version-independent hash (string.GetHashCode is randomised per process),
    // so a placement computed when the market is listed matches the one recomputed when the aircraft is bought.
    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
