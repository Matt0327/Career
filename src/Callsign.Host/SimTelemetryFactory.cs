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
        // The managed adapter compiled in, but the NATIVE SimConnect.dll is P/Invoked lazily on the
        // pump thread — a missing or wrong-bitness native DLL would otherwise show up only as a silent,
        // permanent "Disconnected". Preflight it so we can fall back to the synthetic source loudly.
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(
                "SimConnect.dll", typeof(SimTelemetryFactory).Assembly,
                System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory, out var handle))
        {
            logger.LogWarning("Telemetry source: native SimConnect.dll not loadable next to the app; using the synthetic source.");
            return new FakeTelemetrySource(hz: 4);
        }
        System.Runtime.InteropServices.NativeLibrary.Free(handle);

        try
        {
            // Route the adapter's own diagnostics (unexpected init/protocol errors) into our log.
            var live = new Callsign.SimConnect.Windows.SimConnectTelemetrySource(
                hz: 4, log: msg => logger.LogWarning("SimConnect: {Message}", msg));
            logger.LogInformation("Telemetry source: live MSFS via SimConnect (waits for the sim if it's closed).");
            return live;
        }
        catch (Exception ex) // the stub ctor throws when the managed binaries aren't vendored
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
