namespace Callsign.Core.Time;

/// <summary>
/// Abstraction over "now". Everything time-dependent uses this rather than
/// <see cref="System.DateTimeOffset.UtcNow"/> directly, so wall-clock progression
/// and offline catch-up (brief Phase 3) stay testable and controllable.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Real wall-clock time.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
