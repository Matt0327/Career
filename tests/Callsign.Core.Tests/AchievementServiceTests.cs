using Callsign.Core.Achievements;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// The achievement engine (Phase 5a): reads milestones off existing progress, awards them once, and
/// reports progress for the locked ones.
/// </summary>
public class AchievementServiceTests
{
    private static AchievementService Svc(CallsignDbContext db, FakeClock clock)
        => new(db, clock, new FinanceService(db, clock, EconomyConfig.Default));

    private static (Company Company, Pilot Pilot) Seed(CallsignDbContext db)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        return (company, pilot);
    }

    private static Callsign.Core.Domain.Flight FlightWith(Guid pilotId, double touchdownFpm) => new()
    {
        Id = Guid.NewGuid(),
        FlownByPilotId = pilotId,
        AircraftTitle = "Cessna 172",
        PayoutBreakdownJson = "[]",
        TouchdownFpm = touchdownFpm,
    };

    [Fact]
    public async Task FirstFlight_IsAwarded_AndFrequentFlyerShowsProgress()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        db.Flights.Add(FlightWith(pilot.Id, -120)); // one firm-ish landing
        await db.SaveChangesAsync();

        var views = await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id);

        var first = views.Single(v => v.Key == "first-flight");
        Assert.True(first.Earned);
        Assert.Equal(clock.UtcNow, first.EarnedAt);

        var frequent = views.Single(v => v.Key == "frequent-flyer");
        Assert.False(frequent.Earned);
        Assert.Equal(1, frequent.Progress); // 1 of 25, capped at the target
        Assert.Equal(25, frequent.Target);
    }

    [Fact]
    public async Task Awarding_IsIdempotent_NoDuplicateRows()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        db.Flights.Add(FlightWith(pilot.Id, -50));
        await db.SaveChangesAsync();

        await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id);
        await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id); // a second look must not re-award

        Assert.Equal(1, await db.AchievementAwards.CountAsync(a => a.Key == "first-flight"));
    }

    [Fact]
    public async Task Butter_RequiresALandingWithin60Fpm()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        db.Flights.Add(FlightWith(pilot.Id, -140)); // too firm for butter
        await db.SaveChangesAsync();
        Assert.False((await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id)).Single(v => v.Key == "butter").Earned);

        db.Flights.Add(FlightWith(pilot.Id, -45)); // now grease one on
        await db.SaveChangesAsync();
        Assert.True((await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id)).Single(v => v.Key == "butter").Earned);
    }

    [Fact]
    public async Task EarnedAt_IsStable_AcrossLaterEvaluations()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        db.Flights.Add(FlightWith(pilot.Id, -120));
        await db.SaveChangesAsync();

        var earnedMoment = clock.UtcNow;
        await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id);
        clock.UtcNow = clock.UtcNow.AddDays(3); // time passes, we look again
        var views = await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id);

        Assert.Equal(earnedMoment, views.Single(v => v.Key == "first-flight").EarnedAt);
    }
}
