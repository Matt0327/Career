namespace Callsign.Core.World;

/// <summary>A weather reading plus its provenance (Phase 9b). <see cref="AsOf"/> is the observation time for a
/// live reading, or the query instant for a synthetic one.</summary>
public readonly record struct WeatherReading(Weather Weather, bool IsLive, DateTimeOffset AsOf);

/// <summary>
/// The live-weather seam (Phase 9b). Reads are CACHE-ONLY, SYNCHRONOUS, NON-BLOCKING and NON-THROWING — they
/// serve in-memory state and NEVER touch the network on the read path. Present-tense by contract (they take
/// <c>now</c>, never a historical instant), so this seam is structurally incapable of answering the reconcile
/// loop's past-slot query — that is one of the firewall locks keeping live data out of the autonomous economy.
/// A null result (miss / stale / disabled) means the caller falls back to the synthetic
/// <see cref="WorldOracle.WeatherAt"/>. The live network fetch happens only inside <see cref="Prefetch"/>.
/// </summary>
public interface IWeatherSource
{
    /// <summary>The freshest cached live reading for a field, or null (miss/stale/disabled → caller uses synthetic).
    /// Never blocks, never throws, never issues a network request.</summary>
    WeatherReading? TryObserve(double lat, double lon, DateTimeOffset now);

    /// <summary>Fire-and-forget warm of the cache for a field. Returns instantly; the fetch is detached, hard-
    /// timeout-bounded, and swallows every failure. NEVER awaited on any path, so it can't delay a flight.</summary>
    void Prefetch(double lat, double lon, string? icao);
}

/// <summary>
/// The always-present floor (Phase 9b): a no-op live source. <see cref="TryObserve"/> always returns null and
/// <see cref="Prefetch"/> does nothing, so every surface reads the synthetic model — exactly as Phase 8. It is
/// registered whenever live weather is OFF, which makes "toggle off" and "feed down" the SAME code path.
/// </summary>
public sealed class NullWeatherSource : IWeatherSource
{
    public static readonly NullWeatherSource Instance = new();
    public WeatherReading? TryObserve(double lat, double lon, DateTimeOffset now) => null;
    public void Prefetch(double lat, double lon, string? icao) { }
}
