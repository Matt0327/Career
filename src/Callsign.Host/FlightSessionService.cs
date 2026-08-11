using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Callsign.Core.Economy;
using Callsign.Core.Flight;
using Callsign.SimConnect;
using Microsoft.Extensions.DependencyInjection;

namespace Callsign.Host;

/// <summary>
/// Bridges the live telemetry stream to the game: it feeds every snapshot into a
/// <see cref="FlightTracker"/>, streams the live state to connected WebSocket clients, and when the
/// tracker completes the flight that was begun, it settles that assignment automatically.
/// Singleton — it uses <see cref="IServiceScopeFactory"/> for the scoped settlement work.
/// </summary>
public sealed class FlightSessionService : IDisposable
{
    private readonly ISimTelemetrySource _source;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FlightSessionService> _logger;
    private readonly Action<TelemetrySnapshot> _handler;
    private readonly Action<SimConnectionState> _stateHandler;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    private FlightTracker _tracker = new();
    private Guid? _assignmentId;

    public TelemetrySnapshot? Latest { get; private set; }
    public FlightPhase Phase { get; private set; } = FlightPhase.Parked;
    public SimConnectionState Connection => _source.State;
    public Guid? CurrentAssignmentId => _assignmentId;

    public FlightSessionService(ISimTelemetrySource source, IServiceScopeFactory scopes, ILogger<FlightSessionService> logger)
    {
        _source = source;
        _scopes = scopes;
        _logger = logger;
        _handler = t => _ = FeedAsync(t);
        _stateHandler = OnStateChanged;
        _source.TelemetryReceived += _handler;
        _source.StateChanged += _stateHandler;
    }

    /// <summary>
    /// The link state (Connecting / Connected / Disconnected / SimExited) changed. Push it to the UI
    /// so the HUD reflects reality even when no telemetry frames are flowing — the live SimConnect
    /// source sends nothing until the sim is up, so the HUD must not assume "connected == frames".
    /// </summary>
    private void OnStateChanged(SimConnectionState state)
    {
        _logger.LogInformation("Telemetry link: {State}", state);
        _ = BroadcastAsync(new { type = "state", connection = state.ToString(), phase = Phase.ToString() });
    }

    /// <summary>Start the telemetry source (idempotent at the source level).</summary>
    public Task StartAsync(CancellationToken ct = default) => _source.StartAsync(ct);

    /// <summary>Track a fresh flight for the given accepted assignment; the next landing settles it.</summary>
    public void BeginFlight(Guid assignmentId)
    {
        lock (_gate)
        {
            _tracker = new FlightTracker();
            _assignmentId = assignmentId;
        }
    }

    /// <summary>Feed one telemetry snapshot. Public so it can be driven directly in tests.</summary>
    public async Task FeedAsync(TelemetrySnapshot t)
    {
        FlightRecord? completed = null;
        Guid assignmentToSettle = default;

        lock (_gate)
        {
            Latest = t;
            _tracker.Observe(t);
            Phase = _tracker.Phase;
            if (_tracker.Result is { } record && _assignmentId is { } aid)
            {
                completed = record;
                assignmentToSettle = aid;
                _assignmentId = null; // claim it under the lock so only one feed settles
            }
        }

        await BroadcastAsync(new
        {
            type = "telemetry",
            phase = Phase.ToString(),
            connection = Connection.ToString(),
            alt = t.AltitudeFt,
            ias = t.IndicatedAirspeedKts,
            gs = t.GroundSpeedKts,
            vs = t.VerticalSpeedFpm,
            onGround = t.OnGround,
            lat = t.LatitudeDeg,
            lon = t.LongitudeDeg,
            fuel = t.FuelQuantityLbs,
            title = t.AircraftTitle,
        });

        if (completed is not null)
        {
            var result = await SettleAsync(assignmentToSettle, completed);
            if (result is not null)
                await BroadcastAsync(new
                {
                    type = "settled",
                    assignmentId = assignmentToSettle,
                    payoutCents = result.PayoutCents,
                    xp = result.XpAwarded,
                    payloadMatched = result.PayloadMatched,
                    touchdownFpm = completed.TouchdownFpm,
                });
        }
    }

    private async Task<SettlementResult?> SettleAsync(Guid assignmentId, FlightRecord record)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();
            return await settlement.SettleAsync(assignmentId, record);
        }
        catch
        {
            return null; // never let a settlement error kill the telemetry loop
        }
    }

    /// <summary>Register a WebSocket client and keep it open until it disconnects.</summary>
    public async Task AddClientAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;
        // hand the just-connected client the current link state right away, so a HUD opened while
        // the sim is closed shows "no link" immediately instead of waiting for a frame that never comes
        try { await SendAsync(ws, new { type = "state", connection = Connection.ToString(), phase = Phase.ToString() }); }
        catch { /* client already gone */ }
        try
        {
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                await ws.ReceiveAsync(buffer, ct); // we don't expect input; this just detects close
        }
        catch
        {
            // client went away
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private static async Task SendAsync(WebSocket ws, object message)
    {
        if (ws.State != WebSocketState.Open)
            return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task BroadcastAsync(object message)
    {
        if (_clients.IsEmpty)
            return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    public void Dispose()
    {
        _source.TelemetryReceived -= _handler;
        _source.StateChanged -= _stateHandler;
    }
}
