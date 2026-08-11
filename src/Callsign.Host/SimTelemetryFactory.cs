using Callsign.SimConnect;

namespace Callsign.Host;

/// <summary>
/// Chooses the telemetry source for the running build.
///
/// The Windows build prefers live MSFS telemetry via SimConnect. If the SimConnect binaries
/// aren't vendored, the adapter compiles as a stub whose constructor throws, so we fall back to
/// the synthetic source. The portable (net10.0) build has no SimConnect adapter at all and always
/// uses the synthetic source — that's what keeps CI and the API integration tests cross-platform.
///
/// Note: constructing the real adapter succeeds even when the sim is closed; it owns its own
/// reconnect loop and simply reports <c>Disconnected</c> until MSFS comes up. We only fall back
/// when the adapter itself is unavailable, never merely because the sim isn't running.
/// </summary>
public static class SimTelemetryFactory
{
    public static ISimTelemetrySource Create(ILogger logger)
    {
#if WINDOWS
        try
        {
            var live = new Callsign.SimConnect.Windows.SimConnectTelemetrySource(hz: 4);
            logger.LogInformation("Telemetry source: live MSFS via SimConnect (waits for the sim if it's closed).");
            return live;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Telemetry source: SimConnect unavailable ({Error}); using the synthetic source.", ex.Message);
            return new FakeTelemetrySource(hz: 4);
        }
#else
        logger.LogInformation("Telemetry source: synthetic (portable build).");
        return new FakeTelemetrySource(hz: 4);
#endif
    }
}
