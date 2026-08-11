using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Host;
using Callsign.SimConnect;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Callsign.Host.Tests;

public class FlightSessionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Rotterdam — the destination for every test's assignment.
    private const double EhrdLat = 51.9569, EhrdLon = 4.4372;
    // Amsterdam — ~24 nm from Rotterdam, i.e. well outside the arrival radius.
    private const double EhamLat = 52.3086, EhamLon = 4.7639;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    /// <summary>A non-synthetic source, so the session applies destination geofencing (drives FeedAsync directly).</summary>
    private sealed class RealSourceStub : ISimTelemetrySource
    {
        public SimConnectionState State => SimConnectionState.Connected;
        public bool IsSynthetic => false;
        public event Action<SimConnectionState>? StateChanged { add { } remove { } }
        public event Action<TelemetrySnapshot>? TelemetryReceived { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TelemetrySnapshot Snap(int sec, double alt, double gs, double vs, bool onGround,
        double lat = EhamLat, double lon = EhamLon)
        => new()
        {
            Sequence = sec, CapturedAt = T0.AddSeconds(sec), AltitudeFt = alt,
            IndicatedAirspeedKts = gs, GroundSpeedKts = gs, VerticalSpeedFpm = vs,
            LatitudeDeg = lat, LongitudeDeg = lon, FuelQuantityLbs = 300 - sec,
            OnGround = onGround, AircraftTitle = "Cessna 172 Skyhawk",
        };

    private static ServiceProvider NewProvider(SqliteConnection conn)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CallsignDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton(EconomyConfig.Default);
        services.AddScoped<LedgerService>();
        services.AddScoped<SettlementService>();
        return services.BuildServiceProvider();
    }

    private static async Task<(Guid assignmentId, Guid companyId)> SeedAsync(ServiceProvider sp, bool withDestAirport)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        if (withDestAirport)
            db.Airports.Add(new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = EhrdLat, Longitude = EhrdLon, Kind = AirportKind.LargeAirport });
        var a = new JobAssignment
        {
            Id = Guid.NewGuid(), JobId = Guid.NewGuid(), AccountId = company.Id, PilotId = pilot.Id,
            Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Mail",
            WeightLbs = 400, DistanceNm = 120, RewardQuoteCents = 200_000, XpQuote = 20,
            Status = AssignmentStatus.Accepted, AcceptedAt = T0,
        };
        db.JobAssignments.Add(a);
        await db.SaveChangesAsync();
        return (a.Id, company.Id);
    }

    // takeoff -> short final (-120 fpm) -> touchdown -> stop, landing at (lat, lon).
    private static async Task FlyAndLandAsync(FlightSessionService session, double lat, double lon)
    {
        await session.FeedAsync(Snap(0, 0, 0, 0, onGround: true, lat: EhamLat, lon: EhamLon));
        await session.FeedAsync(Snap(10, 50, 70, 500, onGround: false, lat: EhamLat, lon: EhamLon));
        await session.FeedAsync(Snap(60, 40, 60, -120, onGround: false, lat: lat, lon: lon));
        await session.FeedAsync(Snap(61, 0, 55, 0, onGround: true, lat: lat, lon: lon));
        await session.FeedAsync(Snap(90, 0, 0, 0, onGround: true, lat: lat, lon: lon)); // stop -> flight complete
    }

    [Fact]
    public async Task Landing_AutoSettles_TheBegunAssignment()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var sp = NewProvider(conn);
        var (assignmentId, companyId) = await SeedAsync(sp, withDestAirport: false);

        // Synthetic source: geofencing is skipped, so the canned landing still settles.
        await using var source = new FakeTelemetrySource();
        using var session = new FlightSessionService(source, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlightSessionService>.Instance, EconomyConfig.Default);
        session.BeginFlight(assignmentId);
        await FlyAndLandAsync(session, EhamLat, EhamLon);

        Assert.Null(session.CurrentAssignmentId); // claimed and settled

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
        Assert.Equal(AssignmentStatus.Settled, (await db.JobAssignments.FindAsync(assignmentId))!.Status);
        var flight = await db.Flights.SingleAsync();
        Assert.Equal(-120, flight.TouchdownFpm);
        Assert.Equal(210_000, flight.PayoutCents); // 200000 + 5% landing bonus
        Assert.Equal(210_000, (await db.Companies.FindAsync(companyId))!.CashCents);
    }

    [Fact]
    public async Task Landing_AwayFromDestination_DoesNotSettle_AndKeepsJobOpen()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var sp = NewProvider(conn);
        var (assignmentId, _) = await SeedAsync(sp, withDestAirport: true);

        await using var source = new RealSourceStub(); // real source -> geofencing applies
        using var session = new FlightSessionService(source, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlightSessionService>.Instance, EconomyConfig.Default);
        session.BeginFlight(assignmentId);
        await FlyAndLandAsync(session, EhamLat, EhamLon); // lands at Amsterdam, ~24 nm from Rotterdam

        Assert.Equal(assignmentId, session.CurrentAssignmentId); // still begun — job stays open

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
        Assert.Equal(AssignmentStatus.Accepted, (await db.JobAssignments.FindAsync(assignmentId))!.Status);
        Assert.Empty(await db.Flights.ToListAsync());
    }

    [Fact]
    public async Task Landing_InOwnedAircraft_MovesAirframe_TicksHours_AndMovesPilot()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var sp = NewProvider(conn);
        var (assignmentId, _) = await SeedAsync(sp, withDestAirport: false); // synthetic source skips geofencing

        Guid aircraftId, pilotId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var companyId = await db.Companies.Select(c => c.Id).SingleAsync();
            pilotId = await db.Pilots.Select(p => p.Id).SingleAsync();
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM",
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            aircraftId = inst.Id;
        }

        await using var source = new FakeTelemetrySource();
        using var session = new FlightSessionService(source, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlightSessionService>.Instance, EconomyConfig.Default);
        session.BeginFlight(assignmentId, aircraftId);
        await FlyAndLandAsync(session, EhamLat, EhamLon);

        Assert.Null(session.CurrentAssignmentId); // settled

        using var check = sp.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<CallsignDbContext>();
        var flown = await cdb.AircraftInstances.FindAsync(aircraftId);
        Assert.Equal("EHRD", flown!.LocationIcao);                 // airframe moved to the destination
        Assert.True(flown.AirframeHours > 0);                       // ticked airframe hours
        Assert.Equal(AircraftAvailability.Available, flown.Availability);
        Assert.Equal("EHRD", (await cdb.Pilots.FindAsync(pilotId))!.CurrentIcao); // pilot moved too
        Assert.Equal(aircraftId, (await cdb.Flights.SingleAsync()).AircraftInstanceId);
    }

    [Fact]
    public async Task Landing_AtDestination_Settles()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var sp = NewProvider(conn);
        var (assignmentId, companyId) = await SeedAsync(sp, withDestAirport: true);

        await using var source = new RealSourceStub();
        using var session = new FlightSessionService(source, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlightSessionService>.Instance, EconomyConfig.Default);
        session.BeginFlight(assignmentId);
        await FlyAndLandAsync(session, EhrdLat, EhrdLon); // lands at Rotterdam

        Assert.Null(session.CurrentAssignmentId); // settled

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
        Assert.Equal(AssignmentStatus.Settled, (await db.JobAssignments.FindAsync(assignmentId))!.Status);
        // 200000 base + 5% landing bonus (10000) - $250 landing fee at EHRD (a large airport) = 185000
        Assert.Equal(185_000, (await db.Companies.FindAsync(companyId))!.CashCents);
        Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.AirportFee);
    }
}
