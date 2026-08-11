using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Callsign.Core.Data;
using Callsign.Core.Economy;
using Callsign.Core.Flight;
using Callsign.Core.Geo;
using Callsign.SimConnect;
using Microsoft.EntityFrameworkCore;
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
    private readonly EconomyConfig _config;
    private readonly Action<TelemetrySnapshot> _handler;
    private readonly Action<SimConnectionState> _stateHandler;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    private FlightTracker _tracker = new();
    private Guid? _assignmentId;
    private Guid? _aircraftInstanceId; // the owned airframe flown this leg, if any
    private bool _settling; // guards the async settle so one landing is resolved at a time

    public TelemetrySnapshot? Latest { get; private set; }
    public FlightPhase Phase { get; private set; } = FlightPhase.Parked;
    public SimConnectionState Connection => _source.State;
    public Guid? CurrentAssignmentId => _assignmentId;

    public FlightSessionService(ISimTelemetrySource source, IServiceScopeFactory scopes,
        ILogger<FlightSessionService> logger, EconomyConfig config)
    {
        _source = source;
        _scopes = scopes;
        _logger = logger;
        _config = config;
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

    /// <summary>Track a fresh flight for the given accepted assignment (optionally in an owned airframe);
    /// the next landing at the destination settles it.</summary>
    public void BeginFlight(Guid assignmentId, Guid? aircraftInstanceId = null)
    {
        lock (_gate)
        {
            _tracker = new FlightTracker();
            _assignmentId = assignmentId;
            _aircraftInstanceId = aircraftInstanceId;
        }
    }

    /// <summary>Feed one telemetry snapshot. Public so it can be driven directly in tests.</summary>
    public async Task FeedAsync(TelemetrySnapshot t)
    {
        FlightRecord? completed = null;
        Guid assignmentToSettle = default;
        Guid? aircraftToSettle = null;

        lock (_gate)
        {
            Latest = t;
            _tracker.Observe(t);
            Phase = _tracker.Phase;
            if (_tracker.Result is { } record && _assignmentId is { } aid && !_settling)
            {
                completed = record;
                assignmentToSettle = aid;
                aircraftToSettle = _aircraftInstanceId;
                _settling = true; // resolve this landing before accepting another
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
            await ResolveLandingAsync(assignmentToSettle, aircraftToSettle, completed);
    }

    /// <summary>
    /// A tracked flight finished. If it landed at (near) the job's destination — or the source is
    /// synthetic, which has no real geography — settle it. Otherwise keep the job open and tell the UI
    /// they diverted, so they can take off again and fly on to the destination.
    /// </summary>
    private async Task ResolveLandingAsync(Guid assignmentId, Guid? aircraftInstanceId, FlightRecord completed)
    {
        var arrival = await CheckArrivalAsync(assignmentId, completed);

        if (!arrival.Arrived)
        {
            lock (_gate) { _tracker = new FlightTracker(); _settling = false; } // keep the job; let them fly on
            await BroadcastAsync(new
            {
                type = "diverted",
                assignmentId,
                destIcao = arrival.DestIcao,
                distanceNm = arrival.DistanceNm,
            });
            return;
        }

        var result = await SettleAsync(assignmentId, aircraftInstanceId, completed);
        lock (_gate)
        {
            _tracker = new FlightTracker();
            if (result is not null) { _assignmentId = null; _aircraftInstanceId = null; } // settled → done; on error keep for retry
            _settling = false;
        }
        if (result is not null)
            await BroadcastAsync(new
            {
                type = "settled",
                assignmentId,
                payoutCents = result.PayoutCents,
                xp = result.XpAwarded,
                payloadMatched = result.PayloadMatched,
                promotedTo = result.PromotedTo is { } pr ? Callsign.Core.Progression.RankTiers.Def(pr).DisplayName : null,
                touchdownFpm = completed.TouchdownFpm,
            });
    }

    /// <summary>Did the flight end within the arrival radius of the job's destination airport?</summary>
    private async Task<(bool Arrived, string DestIcao, double DistanceNm)> CheckArrivalAsync(Guid assignmentId, FlightRecord completed)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var a = await db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId);
            if (a is null)
                return (true, "", 0); // nothing to check against — let settlement decide
            if (_source.IsSynthetic)
                return (true, a.DestIcao, 0); // synthetic flight has no real landing position
            var dest = await db.Airports.FirstOrDefaultAsync(x => x.Ident == a.DestIcao);
            if (dest is null)
                return (true, a.DestIcao, 0); // unknown airport — don't strand the player
            var distNm = GeoMath.DistanceNm(completed.ArrivalLat, completed.ArrivalLon, dest.Latitude, dest.Longitude);
            return (distNm <= _config.ArrivalRadiusNm, a.DestIcao, distNm);
        }
        catch
        {
            return (true, "", 0); // never block settlement on a lookup error
        }
    }

    private async Task<SettlementResult?> SettleAsync(Guid assignmentId, Guid? aircraftInstanceId, FlightRecord record)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settlement = scope.ServiceProvider.GetRequiredService<SettlementService>();
            return await settlement.SettleAsync(assignmentId, record, aircraftInstanceId);
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
