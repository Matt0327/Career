namespace Callsign.SimConnect;

/// <summary>
/// A deterministic synthetic telemetry source. Flies a canned profile
/// (ground roll → climb → cruise → descent → touchdown, then repeats) so that every
/// company-loop feature — and this PoC — works with MSFS closed. No SimConnect
/// dependency; runs on any OS.
/// </summary>
public sealed class FakeTelemetrySource : ISimTelemetrySource
{
    private readonly string _title;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SimConnectionState _state = SimConnectionState.Disconnected;

    public FakeTelemetrySource(string aircraftTitle = "Cessna 172 Skyhawk (fake)", int hz = 4)
    {
        _title = aircraftTitle;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(hz, 1, 30));
    }

    public SimConnectionState State => _state;
    public bool IsSynthetic => true;
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
        SetState(SimConnectionState.Connecting);
        SetState(SimConnectionState.Connected);
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        long seq = 0;
        const double sortieSeconds = 600.0; // one full synthetic flight, then loop
        const double cruiseFt = 2900.0;     // peak altitude; keeps rates realistic (~900 fpm)

        try
        {
            while (!ct.IsCancellationRequested)
            {
                double t = (seq * _interval.TotalSeconds) % sortieSeconds;
                double frac = t / sortieSeconds;                       // 0..1 through the sortie
                double altitude = Math.Max(0, cruiseFt * Math.Sin(frac * Math.PI));
                double vs = cruiseFt * Math.PI / sortieSeconds * Math.Cos(frac * Math.PI) * 60;
                bool onGround = altitude < 5;
                double ias = onGround ? Math.Min(60, t * 8) : 110 + 20 * Math.Sin(frac * Math.PI);
                double gs = onGround ? ias : ias + 15;
                // Model the aircraft as SECURED only when fully stopped on the ground (Phase 12): brakes set,
                // engine shut down. Moving or airborne = engine running, brakes off. This lets the synthetic
                // leg complete the realistic way — at the stop, secured — instead of the moment it merely halts.
                bool stopped = onGround && gs < 3;

                TelemetryReceived?.Invoke(new TelemetrySnapshot
                {
                    Sequence = seq++,
                    CapturedAt = DateTimeOffset.UtcNow,
                    AltitudeFt = altitude,
                    IndicatedAirspeedKts = ias,
                    GroundSpeedKts = gs,
                    VerticalSpeedFpm = onGround ? 0 : vs,
                    LatitudeDeg = 52.0 + frac * 1.5,   // drift a synthetic ground track
                    LongitudeDeg = 4.0 + frac * 2.5,
                    FuelQuantityLbs = 500 - frac * 120,
                    OnGround = onGround,
                    AircraftTitle = _title,
                    AltitudeAglFt = altitude, // no synthetic terrain — AGL tracks indicated altitude
                    ParkingBrakeSet = stopped,
                    EngineRunning = !stopped,
                });

                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        try
        {
            if (_loop is not null)
                await _loop;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        SetState(SimConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
