using Callsign.SimConnect;

namespace Callsign.SimConnect.Windows;

#if SIMCONNECT_AVAILABLE
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

/// <summary>
/// Live telemetry from MSFS via the official SimConnect managed interop.
///
/// Windows-only; requires <c>Microsoft.FlightSimulator.SimConnect.dll</c> and the native
/// <c>SimConnect.dll</c> (fetched into /vendor by scripts/fetch-simconnect.ps1).
///
/// Headless design: SimConnect is pumped on a dedicated background thread that waits on an
/// <see cref="EventWaitHandle"/> (no window/message loop needed for a console or service). The
/// source owns its reconnect loop and survives the sim exiting and restarting (brief §10.3). All
/// SimConnect calls happen on the pump thread, since the managed wrapper is not thread-safe.
/// </summary>
public sealed class SimConnectTelemetrySource : ISimTelemetrySource
{
    private const int WmUserSimConnect = 0x0402;

    private enum Definitions { AircraftData }
    private enum Requests { AircraftData }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftData
    {
        public double AltitudeFt;
        public double IndicatedAirspeedKts;
        public double GroundSpeedKts;
        public double VerticalSpeedFpm;
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double FuelQuantityLbs;
        public int OnGround;
        // Phase 7b substrate — order here MUST match the AddToDataDefinition call order below.
        public double GForce;
        public double PitchDeg;
        public double BankDeg;
        public double HeadingDegTrue;
        public double AltitudeAglFt;
        public double FlapsPercent;
        public double GearPercent;
        public int StallWarning;
        public int OverspeedWarning;
        public double SimRate;
        public int SlewActive;
        // Phase 8 ambient weather — order here MUST match the AddToDataDefinition call order below.
        public double AmbientWindKts;
        public double AmbientWindDirDeg;
        public double AmbientVisibilityMeters;
        // Phase 9c authoritative touchdown — order here MUST match the AddToDataDefinition call order below.
        public double TouchdownNormalVelocityFps;
        public double TouchdownLateralVelocityFps;
        // Phase 9d weight & balance — order here MUST match the AddToDataDefinition call order below.
        public double TotalWeightLbs;
        public double MaxGrossWeightLbs;
        public double CgPercent;
        public double CgFwdLimit;
        public double CgAftLimit;
        // Phase 9e engine damage — order here MUST match the AddToDataDefinition call order below.
        public double EngineDamagePercent;
        // Phase 10b approach precision — order here MUST match the AddToDataDefinition call order below.
        public double ApproachCrossTrackMeters;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }

    private readonly TimeSpan _interval;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
    private readonly Action<string>? _log;

    private Microsoft.FlightSimulator.SimConnect.SimConnect? _sc;
    private EventWaitHandle? _hEvent;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private long _seq;
    private SimConnectionState _state = SimConnectionState.Disconnected;

    /// <param name="log">
    /// Optional diagnostic sink. The benign "sim not running yet" case stays quiet, but genuine,
    /// permanent failures (missing/wrong-bitness native SimConnect.dll, a SimConnect protocol
    /// exception) are surfaced here so live telemetry never fails silently.
    /// </param>
    public SimConnectTelemetrySource(int hz = 4, Action<string>? log = null)
    {
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(hz, 1, 30));
        _log = log;
    }

    public SimConnectionState State => _state;
    public bool IsSynthetic => false;
    public event Action<SimConnectionState>? StateChanged;
    public event Action<TelemetrySnapshot>? TelemetryReceived;

    private void SetState(SimConnectionState s)
    {
        if (_state == s)
            return;
        _state = s;
        StateChanged?.Invoke(s);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _thread = new Thread(() => PumpLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "SimConnect-Pump",
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void PumpLoop(CancellationToken ct)
    {
        _hEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(SimConnectionState.Connecting);
                _sc = new Microsoft.FlightSimulator.SimConnect.SimConnect(
                    "Callsign", IntPtr.Zero, WmUserSimConnect, _hEvent, 0);
                WireHandlers(_sc);
                RegisterDataDefinition(_sc);
                SetState(SimConnectionState.Connected);

                long nextPoll = Environment.TickCount64;
                while (!ct.IsCancellationRequested && _sc is not null)
                {
                    if (_hEvent.WaitOne(50))
                        _sc?.ReceiveMessage();

                    long now = Environment.TickCount64;
                    if (_sc is not null && now >= nextPoll)
                    {
                        _sc.RequestDataOnSimObject(
                            Requests.AircraftData, Definitions.AircraftData,
                            Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                            SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                        nextPoll = now + (long)_interval.TotalMilliseconds;
                    }
                }
            }
            catch (COMException)
            {
                // sim not running yet, or the connection dropped — expected; reconnect quietly
            }
            catch (Exception ex)
            {
                // Unexpected and usually permanent (missing/wrong-bitness native SimConnect.dll,
                // an unloadable managed interop, a bad data definition). Never let the pump thread
                // die — but don't swallow it silently, or a broken install is indistinguishable
                // from a closed sim (both just report Disconnected forever).
                _log?.Invoke($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                DisposeConnection();
            }

            if (ct.IsCancellationRequested)
                break;

            SetState(SimConnectionState.Disconnected);
            ct.WaitHandle.WaitOne(_reconnectDelay); // back off before retrying while the sim is closed
        }

        _hEvent?.Dispose();
        _hEvent = null;
    }

    private void WireHandlers(Microsoft.FlightSimulator.SimConnect.SimConnect sc)
    {
        sc.OnRecvQuit += (_, _) =>
        {
            // user closed the sim; drop the connection and let the loop reconnect
            SetState(SimConnectionState.SimExited);
            var old = _sc;
            _sc = null;
            old?.Dispose();
        };
        sc.OnRecvException += (_, e) =>
        {
            // a bad request shouldn't kill telemetry, but name the SimConnect exception that fired
            _log?.Invoke($"protocol exception: {(SIMCONNECT_EXCEPTION)e.dwException}");
        };
        sc.OnRecvSimobjectData += OnData;
    }

    private static void RegisterDataDefinition(Microsoft.FlightSimulator.SimConnect.SimConnect sc)
    {
        uint unused = Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_UNUSED;
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE ALTITUDE", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "AIRSPEED INDICATED", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "GROUND VELOCITY", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "VERTICAL SPEED", "feet per minute",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE LATITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE LONGITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "FUEL TOTAL QUANTITY WEIGHT", "pounds",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "SIM ON GROUND", "bool",
            SIMCONNECT_DATATYPE.INT32, 0f, unused);
        // Phase 7b substrate — order here MUST match the struct field order above.
        sc.AddToDataDefinition(Definitions.AircraftData, "G FORCE", "GForce",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE PITCH DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE BANK DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE HEADING DEGREES TRUE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE ALT ABOVE GROUND", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "FLAPS HANDLE PERCENT", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "GEAR TOTAL PCT EXTENDED", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "STALL WARNING", "bool",
            SIMCONNECT_DATATYPE.INT32, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "OVERSPEED WARNING", "bool",
            SIMCONNECT_DATATYPE.INT32, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "SIMULATION RATE", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "IS SLEW ACTIVE", "bool",
            SIMCONNECT_DATATYPE.INT32, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "AMBIENT WIND VELOCITY", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "AMBIENT WIND DIRECTION", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "AMBIENT VISIBILITY", "meters",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        // Phase 9c — the sim's captured touchdown state (order matches the struct fields above).
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE TOUCHDOWN NORMAL VELOCITY", "feet per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "PLANE TOUCHDOWN LATERAL VELOCITY", "feet per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        // Phase 9d — weight & balance (order matches the struct fields above).
        sc.AddToDataDefinition(Definitions.AircraftData, "TOTAL WEIGHT", "pounds",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "MAX GROSS WEIGHT", "pounds",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "CG PERCENT", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "CG FWD LIMIT", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "CG AFT LIMIT", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        // Phase 9e — the sim's own cumulative engine damage (primary engine; a multi-engine max is a later
        // refinement). Explicit "percent" unit so the reading is 0..100, never an SDK default trap (L10).
        sc.AddToDataDefinition(Definitions.AircraftData, "GENERAL ENG DAMAGE PERCENT:1", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        // Phase 10b — cross-track from the active approach centreline. Explicit "meters" (the SDK default);
        // 0 when no approach/GPS leg is active → the tracker treats it as on-centreline (L10). Converted to feet below.
        sc.AddToDataDefinition(Definitions.AircraftData, "GPS WP CROSS TRK", "meters",
            SIMCONNECT_DATATYPE.FLOAT64, 0f, unused);
        sc.AddToDataDefinition(Definitions.AircraftData, "TITLE", null,
            SIMCONNECT_DATATYPE.STRING256, 0f, unused);
        sc.RegisterDataDefineStruct<AircraftData>(Definitions.AircraftData);
    }

    private void OnData(Microsoft.FlightSimulator.SimConnect.SimConnect sc, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if ((Requests)data.dwRequestID != Requests.AircraftData)
            return;
        if (data.dwData is null || data.dwData.Length == 0)
            return;

        var d = (AircraftData)data.dwData[0];
        TelemetryReceived?.Invoke(new TelemetrySnapshot
        {
            Sequence = Interlocked.Increment(ref _seq),
            CapturedAt = DateTimeOffset.UtcNow,
            AltitudeFt = d.AltitudeFt,
            IndicatedAirspeedKts = d.IndicatedAirspeedKts,
            GroundSpeedKts = d.GroundSpeedKts,
            VerticalSpeedFpm = d.VerticalSpeedFpm,
            LatitudeDeg = d.LatitudeDeg,
            LongitudeDeg = d.LongitudeDeg,
            FuelQuantityLbs = d.FuelQuantityLbs,
            OnGround = d.OnGround != 0,
            AircraftTitle = d.Title ?? string.Empty,
            GForce = d.GForce,
            PitchDeg = d.PitchDeg,
            BankDeg = d.BankDeg,
            HeadingDegTrue = d.HeadingDegTrue,
            AltitudeAglFt = d.AltitudeAglFt,
            FlapsPercent = d.FlapsPercent,
            // GEAR TOTAL PCT EXTENDED reports either 0..1 or 0..100 depending on unit resolution;
            // normalise to 0..100 so downstream never mistakes a down-and-locked gear for retracted.
            GearPercent = d.GearPercent <= 1.5 ? d.GearPercent * 100 : d.GearPercent,
            StallWarning = d.StallWarning != 0,
            OverspeedWarning = d.OverspeedWarning != 0,
            SimRate = d.SimRate,
            SlewActive = d.SlewActive != 0,
            AmbientWindKts = d.AmbientWindKts,
            AmbientWindDirDeg = d.AmbientWindDirDeg,
            AmbientVisibilitySm = d.AmbientVisibilityMeters / 1609.344, // meters → statute miles
            TouchdownNormalVelocityFps = d.TouchdownNormalVelocityFps,
            TouchdownLateralVelocityFps = d.TouchdownLateralVelocityFps,
            TotalWeightLbs = d.TotalWeightLbs,
            MaxGrossWeightLbs = d.MaxGrossWeightLbs,
            CgPercent = d.CgPercent,
            CgFwdLimit = d.CgFwdLimit,
            CgAftLimit = d.CgAftLimit,
            EngineDamagePercent = d.EngineDamagePercent,
            ApproachCrossTrackFt = d.ApproachCrossTrackMeters * 3.28084, // meters → feet
        });
    }

    private void DisposeConnection()
    {
        var sc = _sc;
        _sc = null;
        try { sc?.Dispose(); }
        catch { /* ignore */ }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        SetState(SimConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
#else

/// <summary>
/// Build-time stub used when the SimConnect binaries are absent from /vendor
/// (see scripts/fetch-simconnect.ps1). Lets the whole solution compile on any machine; the real
/// implementation lights up once the DLLs exist. Constructing it throws, so callers fall back to
/// <see cref="FakeTelemetrySource"/>.
/// </summary>
public sealed class SimConnectTelemetrySource : ISimTelemetrySource
{
    public SimConnectTelemetrySource(int hz = 4, Action<string>? log = null)
        => throw new PlatformNotSupportedException(
            "SimConnect binaries not found. Install the MSFS 2024 SDK, run " +
            "scripts/fetch-simconnect.ps1, then rebuild to enable live telemetry.");

    public SimConnectionState State => SimConnectionState.Disconnected;
    public bool IsSynthetic => false;
    public event Action<SimConnectionState>? StateChanged { add { } remove { } }
    public event Action<TelemetrySnapshot>? TelemetryReceived { add { } remove { } }
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
