namespace Callsign.SimConnect;

/// <summary>
/// Abstraction over the source of live flight telemetry.
///
/// The real implementation (<c>SimConnectTelemetrySource</c>, Windows-only) talks
/// to MSFS via SimConnect. <see cref="FakeTelemetrySource"/> replays a synthetic
/// flight so the whole app — and the PoC — runs with the sim closed (brief
/// acceptance criterion #6). Everything downstream depends only on this interface.
/// </summary>
public interface ISimTelemetrySource : IAsyncDisposable
{
    /// <summary>Current connection state.</summary>
    SimConnectionState State { get; }

    /// <summary>
    /// True for a stand-in source with no real-world geography (the synthetic flight profile).
    /// The game skips destination geofencing for a synthetic source, since its landing position
    /// is canned — that keeps the whole loop playable and testable with the sim closed.
    /// </summary>
    bool IsSynthetic { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event Action<SimConnectionState>? StateChanged;

    /// <summary>Raised at the poll rate with the latest telemetry frame.</summary>
    event Action<TelemetrySnapshot>? TelemetryReceived;

    /// <summary>
    /// Begin connecting and polling. Returns immediately; the source owns its own
    /// reconnect loop and survives the sim exiting and restarting.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop polling and release the connection.</summary>
    Task StopAsync();
}
