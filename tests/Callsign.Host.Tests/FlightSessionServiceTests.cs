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

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private static TelemetrySnapshot Snap(int sec, double alt, double gs, double vs, bool onGround)
        => new()
        {
            Sequence = sec, CapturedAt = T0.AddSeconds(sec), AltitudeFt = alt,
            IndicatedAirspeedKts = gs, GroundSpeedKts = gs, VerticalSpeedFpm = vs,
            LatitudeDeg = 52.3, LongitudeDeg = 4.76, FuelQuantityLbs = 300 - sec,
            OnGround = onGround, AircraftTitle = "Cessna 172 Skyhawk",
        };

    [Fact]
    public async Task Landing_AutoSettles_TheBegunAssignment()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<CallsignDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton(EconomyConfig.Default);
        services.AddScoped<LedgerService>();
        services.AddScoped<SettlementService>();
        await using var sp = services.BuildServiceProvider();

        Guid assignmentId, companyId;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            await db.Database.EnsureCreatedAsync();

            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
            db.Companies.Add(company);
            db.Pilots.Add(pilot);
            db.JobAssignments.Add(new JobAssignment
            {
                Id = Guid.NewGuid(), JobId = Guid.NewGuid(), AccountId = company.Id, PilotId = pilot.Id,
                Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Mail",
                WeightLbs = 400, DistanceNm = 120, RewardQuoteCents = 200_000, XpQuote = 20,
                Status = AssignmentStatus.Accepted, AcceptedAt = T0,
            });
            await db.SaveChangesAsync();
            companyId = company.Id;
            assignmentId = await db.JobAssignments.Select(a => a.Id).SingleAsync();
        }

        await using var source = new FakeTelemetrySource();
        using var session = new FlightSessionService(source, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlightSessionService>.Instance);
        session.BeginFlight(assignmentId);

        // parked -> takeoff -> short final (-120 fpm) -> touchdown -> stop
        await session.FeedAsync(Snap(0, 0, 0, 0, onGround: true));
        await session.FeedAsync(Snap(10, 50, 70, 500, onGround: false));
        await session.FeedAsync(Snap(60, 40, 60, -120, onGround: false));
        await session.FeedAsync(Snap(61, 0, 55, 0, onGround: true));
        await session.FeedAsync(Snap(90, 0, 0, 0, onGround: true)); // stop -> flight complete -> auto-settle

        Assert.Null(session.CurrentAssignmentId); // claimed and settled

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
            var assignment = await db.JobAssignments.FindAsync(assignmentId);
            Assert.Equal(AssignmentStatus.Settled, assignment!.Status);

            var flight = await db.Flights.SingleAsync();
            Assert.Equal(-120, flight.TouchdownFpm);
            Assert.Equal(210_000, flight.PayoutCents); // 200000 + 5% landing bonus

            var company = await db.Companies.FindAsync(companyId);
            Assert.Equal(210_000, company!.CashCents); // paid, via the ledger
        }
    }
}
