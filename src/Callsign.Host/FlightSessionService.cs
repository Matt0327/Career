using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Flight;
using Callsign.Core.Geo;
using Callsign.Core.Progression;
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
    private IReadOnlyList<Guid> _alsoAssignmentIds = Array.Empty<Guid>(); // Phase 13 — extra jobs flown on the same leg
    private Guid? _aircraftInstanceId; // the owned airframe flown this leg, if any
    private QualClass? _checkClass;    // a check-flight in progress for this class (Phase 3d), if any
    private bool _freeFlight;          // a free flight (Phase 12): tracked + logged, but no job to settle
    private bool _settling; // guards the async settle so one landing is resolved at a time
    private int _sentEventCount; // how many of the tracker's events have already been streamed live (Phase 7a)
    // The armed leg's destination, resolved once when the flight begins, so every telemetry frame can report the
    // live distance to it and whether we're inside the arrival sector (Phase 13 — a VISIBLE geofence, NeoFly-style).
    private string _destIcao = "";
    private double _destLat, _destLon;
    private bool _destHasCoords;

    public TelemetrySnapshot? Latest { get; private set; }
    public FlightPhase Phase { get; private set; } = FlightPhase.Parked;
    public SimConnectionState Connection => _source.State;
    public Guid? CurrentAssignmentId => _assignmentId;
    public bool FreeFlightActive => _freeFlight;                 // a free flight is armed (for tab-switch restore)
    public IReadOnlyList<Guid> AlsoAssignmentIds => _alsoAssignmentIds; // extra same-leg jobs, so a restore can rebuild the combo

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

    /// <summary>Push a user-facing notification to every connected client (Phase 13) — e.g. an autonomous crew
    /// leg that just banked. Reuses the telemetry socket, so it reaches the app on any tab.</summary>
    public Task NotifyAsync(string text) => BroadcastAsync(new { type = "notify", text });

    /// <summary>Start the telemetry source (idempotent at the source level).</summary>
    public Task StartAsync(CancellationToken ct = default) => _source.StartAsync(ct);

    /// <summary>Track a fresh flight for the given accepted assignment (optionally in an owned airframe);
    /// the next landing at the destination settles it.</summary>
    public void BeginFlight(Guid assignmentId, Guid? aircraftInstanceId = null, IReadOnlyList<Guid>? alsoSettle = null)
    {
        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            _assignmentId = assignmentId;
            // Phase 13 — extra same-destination jobs flown together on this one leg; each settles with the same
            // landing when the primary does (validated at the begin endpoint: shared origin+dest, combined fit).
            _alsoAssignmentIds = alsoSettle is { Count: > 0 } ? alsoSettle.ToArray() : Array.Empty<Guid>();
            _aircraftInstanceId = aircraftInstanceId;
            _checkClass = null; // a job flight and a check-flight are mutually exclusive
            _freeFlight = false;
            _destIcao = ""; _destHasCoords = false; // resolved just below, off the DB
        }
        _ = ResolveDestinationAsync(assignmentId); // fire-and-forget: fills the sector coords within a frame or two
    }

    /// <summary>Resolve the armed leg's destination airport coordinates once, so FeedAsync can broadcast the live
    /// distance-to-destination and in-sector flag every frame (a visible arrival geofence). Best-effort.</summary>
    private async Task ResolveDestinationAsync(Guid assignmentId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var a = await db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId);
            if (a is null) return;
            var dest = await db.Airports.FirstOrDefaultAsync(x => x.Ident == a.DestIcao);
            lock (_gate)
            {
                if (_assignmentId != assignmentId) return; // a different leg was armed meanwhile
                _destIcao = a.DestIcao;
                if (dest is not null) { _destLat = dest.Latitude; _destLon = dest.Longitude; _destHasCoords = true; }
            }
        }
        catch { /* the sector readout is best-effort; settlement's own CheckArrivalAsync is the source of truth */ }
    }

    /// <summary>Abandon whatever leg is armed (job / check / free) WITHOUT settling — e.g. the player ended the
    /// flight in the sim and switched to a different aircraft, so the armed job must not be flown in the wrong
    /// plane. Resets the tracker and clears the arm; the job stays open to fly again.</summary>
    public void AbortFlight()
    {
        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            _assignmentId = null; _aircraftInstanceId = null; _alsoAssignmentIds = Array.Empty<Guid>();
            _checkClass = null; _freeFlight = false;
            _destIcao = ""; _destHasCoords = false;
        }
    }

    /// <summary>Begin a check-flight for a licence class (Phase 3d): the next landing is graded, not settled.</summary>
    public void BeginCheckFlight(QualClass cls)
    {
        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            _assignmentId = null;
            _aircraftInstanceId = null;
            _checkClass = cls;
            _freeFlight = false;
            _destIcao = ""; _destHasCoords = false; // a check-flight has no job destination sector
        }
    }

    /// <summary>Begin a FREE flight (Phase 12): no job, no check — just fly. The next completed leg is tracked
    /// and written to the logbook with no payout, so a bit of practice or sightseeing still counts and is scored.</summary>
    public void BeginFreeFlight()
    {
        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            _assignmentId = null;
            _aircraftInstanceId = null;
            _checkClass = null;
            _freeFlight = true;
            _destIcao = ""; _destHasCoords = false; // a free flight has no job destination sector
        }
    }

    /// <summary>Feed one telemetry snapshot. Public so it can be driven directly in tests.</summary>
    public async Task FeedAsync(TelemetrySnapshot t)
    {
        FlightRecord? completed = null;
        Guid assignmentToSettle = default;
        Guid? aircraftToSettle = null;
        QualClass? checkToGrade = null;
        bool freeToLog = false;
        List<FlightEvent> newEvents = [];
        bool active;
        string destIcaoLocal = ""; bool destHasCoordsLocal = false; double destLatLocal = 0, destLonLocal = 0;

        lock (_gate)
        {
            Latest = t;
            destIcaoLocal = _destIcao; destHasCoordsLocal = _destHasCoords; destLatLocal = _destLat; destLonLocal = _destLon;
            // The tracker — the moving map, the coaching, the scoring — only runs while a flight or check is
            // ARMED. Idle, we don't feed it: no phantom flight, no out-of-nowhere coaching, no map wandering
            // off across the sea from the synthetic source that's always streaming (Phase 12). The HUD still
            // gets frames (below) so the link badge stays honest.
            active = _assignmentId is not null || _checkClass is not null || _freeFlight;
            if (active)
            {
                _tracker.Observe(t);
                Phase = _tracker.Phase;
                // Capture any scored events the tracker just produced so we can stream the REAL story of the
                // flight live (Phase 7a) — takeoff, the touchdown and its quality, a taxi overspeed. Before
                // this the events were computed and thrown away and the client narrated a fabricated log.
                for (int i = _sentEventCount; i < _tracker.Events.Count; i++)
                    newEvents.Add(_tracker.Events[i]);
                _sentEventCount = _tracker.Events.Count;
                if (_tracker.Result is { } record && !_settling)
                {
                    if (_assignmentId is { } aid)
                    {
                        completed = record;
                        assignmentToSettle = aid;
                        aircraftToSettle = _aircraftInstanceId;
                        _settling = true; // resolve this landing before accepting another
                    }
                    else if (_checkClass is { } cls)
                    {
                        completed = record;
                        checkToGrade = cls;
                        _settling = true;
                    }
                    else if (_freeFlight)
                    {
                        completed = record;
                        freeToLog = true;
                        _settling = true;
                    }
                }
            }
            else
            {
                Phase = FlightPhase.Parked; // nothing armed — the HUD reads a calm, parked state
            }
        }

        // With no flight armed, the synthetic source is streaming a canned flight that isn't real — zero its
        // position and gauges so the map shows no aircraft and the HUD reads parked. A real sim's live position
        // is genuine, so it's passed through even when idle (you can see your aircraft on the ramp).
        bool showLive = active || !_source.IsSynthetic;
        // Live distance to the destination airport + whether we're inside the arrival sector, so the Flight tab can
        // draw the geofence and only say "on approach" when we're genuinely near the field (Phase 13, bugs #7/#8).
        double? distToDestNm = null;
        bool inSector = false;
        if (active && destHasCoordsLocal && showLive && (t.LatitudeDeg != 0 || t.LongitudeDeg != 0))
        {
            distToDestNm = GeoMath.DistanceNm(t.LatitudeDeg, t.LongitudeDeg, destLatLocal, destLonLocal);
            inSector = distToDestNm <= _config.ArrivalRadiusNm;
        }
        await BroadcastAsync(new
        {
            type = "telemetry",
            phase = Phase.ToString(),
            connection = Connection.ToString(),
            destIcao = destIcaoLocal,
            distToDestNm,
            inSector,
            arrivalRadiusNm = _config.ArrivalRadiusNm,
            alt = showLive ? t.AltitudeFt : 0.0,
            ias = showLive ? t.IndicatedAirspeedKts : 0.0,
            gs = showLive ? t.GroundSpeedKts : 0.0,
            vs = showLive ? t.VerticalSpeedFpm : 0.0,
            onGround = showLive ? t.OnGround : true,
            lat = showLive ? t.LatitudeDeg : 0.0,
            lon = showLive ? t.LongitudeDeg : 0.0,
            fuel = showLive ? t.FuelQuantityLbs : 0.0,
            title = t.AircraftTitle,
            // Shutdown-checklist state (Phase 13): lets the Flight tab show land → brake → engine-off live.
            parkingBrake = showLive && t.ParkingBrakeSet,
            engineRunning = showLive && t.EngineRunning,
            // Phase 15 — the sim's real weights, so the Flight tab can show the NeoFly-style loaded-payload readout
            // and gate on the cargo/pax actually being loaded in the sim. Loaded payload = Total − Empty − fuel.
            totalWeightLbs = showLive ? t.TotalWeightLbs : 0.0,
            emptyWeightLbs = showLive ? t.EmptyWeightLbs : 0.0,
            maxGrossWeightLbs = showLive ? t.MaxGrossWeightLbs : 0.0,
        });

        // Stream the real scored moments as they happen (Phase 7a). Same events that get persisted at
        // settlement — the live log and the logbook now tell the identical, true story.
        foreach (var ev in newEvents)
            await BroadcastAsync(new
            {
                type = "event",
                severity = ev.Severity.ToString(),
                message = ev.Message,
                at = ev.At,
                phase = Phase.ToString(),
            });

        if (completed is not null)
        {
            if (checkToGrade is { } cls)
                await ResolveCheckFlightAsync(cls, completed);
            else if (freeToLog)
                await ResolveFreeFlightAsync(completed);
            else
                await ResolveLandingAsync(assignmentToSettle, aircraftToSettle, completed);
        }
    }

    /// <summary>A check-flight landing finished: grade it, award the class on a pass, tell the UI.</summary>
    private async Task ResolveCheckFlightAsync(QualClass cls, FlightRecord completed)
    {
        CheckFlightResult? result = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is not null)
            {
                var check = scope.ServiceProvider.GetRequiredService<CheckFlightService>();
                result = await check.AttemptAsync(pilot.CompanyId, pilot.Id, cls, completed);
            }
        }
        catch
        {
            result = null; // never let a grading error kill the telemetry loop
        }

        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            if (result is not null) _checkClass = null; // graded → done; on error keep for retry
            _settling = false;
        }

        if (result is not null)
            await BroadcastAsync(new
            {
                type = "checkflight",
                @class = cls.ToString(),
                className = QualificationClasses.Def(cls).DisplayName,
                passed = result.Passed,
                stars = result.Stars,
                feeCents = result.FeeCents,
                touchdownFpm = completed.TouchdownFpm,
            });
    }

    /// <summary>A free flight finished: write it to the logbook with no payout (it still scores + counts), then
    /// tell the UI. No job, no location change — pure practice/sightseeing that the game monitors and logs.</summary>
    private async Task ResolveFreeFlightAsync(FlightRecord completed)
    {
        Guid? flightId = null;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var pilot = await db.Pilots.FirstOrDefaultAsync();
            if (pilot is not null)
            {
                bool scored = completed.Scored;
                var f = new Flight
                {
                    Id = Guid.NewGuid(), JobAssignmentId = null, FlownByPilotId = pilot.Id,
                    AircraftTitle = completed.AircraftTitle, DepartedAt = completed.DepartedAt, ArrivedAt = completed.ArrivedAt,
                    TouchdownFpm = completed.TouchdownFpm, DistanceNm = completed.DistanceNm, FuelUsedLbs = completed.FuelUsedLbs,
                    PayoutCents = 0, Xp = 0, PayoutBreakdownJson = "[]", SettledAt = completed.ArrivedAt,
                    TouchdownFpmWorst3 = scored ? completed.TouchdownFpmWorst3 : null,
                    TouchdownG = scored ? completed.TouchdownG : null,
                    LandingScore = scored ? completed.LandingScore : null,
                    ApproachScore = scored ? completed.ApproachScore : null,
                    OverallScore = scored ? completed.OverallScore : null,
                    ComfortScore = scored ? completed.ComfortScore : null,
                    StabilizedApproach = scored ? completed.StabilizedApproach : null,
                    ViolationPoints = scored ? completed.ViolationPoints : null,
                    ScoreValid = scored ? completed.ScoreValid : null,
                };
                db.Flights.Add(f);
                await db.SaveChangesAsync();
                flightId = f.Id;
            }
        }
        catch
        {
            flightId = null; // never let a logging error kill the telemetry loop
        }

        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            if (flightId is not null) _freeFlight = false; // logged → done; on error keep for retry
            _settling = false;
        }

        if (flightId is not null)
            await BroadcastAsync(new
            {
                type = "freeflight",
                flightId,
                touchdownFpm = completed.TouchdownFpm,
                overallScore = completed.Scored ? completed.OverallScore : (int?)null,
            });
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
        // Phase 13 — settle any extra jobs flown on the same leg (same destination) with THIS landing. Each is
        // independent (its own frozen reward + ledger, dedupe-keyed); we sum pay + XP for the end-of-flight card.
        long extraPay = 0; int extraXp = 0;
        var extras = _alsoAssignmentIds;
        if (result is not null && extras.Count > 0)
            foreach (var extraId in extras)
            {
                try
                {
                    var er = await SettleAsync(extraId, aircraftInstanceId, completed);
                    if (er is not null) { extraPay += er.PayoutCents; extraXp += er.XpAwarded; }
                }
                catch { /* one bad companion job never blocks the primary settlement */ }
            }
        lock (_gate)
        {
            _tracker = new FlightTracker(); _sentEventCount = 0;
            if (result is not null) { _assignmentId = null; _aircraftInstanceId = null; _alsoAssignmentIds = Array.Empty<Guid>(); } // settled → done; on error keep for retry
            _settling = false;
        }
        if (result is not null)
            await BroadcastAsync(new
            {
                type = "settled",
                assignmentId,
                flightId = result.FlightId, // Phase 12 — so the end-of-flight card can show the score + coaching debrief
                payoutCents = result.PayoutCents + extraPay,
                xp = result.XpAwarded + extraXp,
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
