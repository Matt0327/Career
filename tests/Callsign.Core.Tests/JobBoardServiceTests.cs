using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class JobBoardServiceTests
{
    private static Airport A(string id, string name, double lat, double lon, AirportKind kind)
        => new() { Ident = id, IcaoCode = id, Name = name, Latitude = lat, Longitude = lon, Kind = kind };

    private static async Task SeedAirportsAsync(CallsignDbContext db)
    {
        db.Airports.AddRange(
            A("EHAM", "Amsterdam Schiphol", 52.3086, 4.7639, AirportKind.LargeAirport),
            A("EHRD", "Rotterdam", 51.9569, 4.4372, AirportKind.LargeAirport),
            A("EHEH", "Eindhoven", 51.4501, 5.3745, AirportKind.LargeAirport),
            A("EHGG", "Groningen", 53.1197, 6.5794, AirportKind.MediumAirport),
            A("EGLL", "London Heathrow", 51.4706, -0.4619, AirportKind.LargeAirport),
            A("EHHELI", "Rotterdam Heliport", 51.95, 4.45, AirportKind.Heliport), // not landable
            // A nearby field with no ICAO code (OurAirports placeholder): must never be a destination,
            // even though it's the closest landable airport.
            new Airport { Ident = "NL-0099", IcaoCode = null, Name = "Tiny Airstrip", Latitude = 52.50, Longitude = 4.95, Kind = AirportKind.SmallAirport });
        await db.SaveChangesAsync();
    }

    private static JobBoardService Board(CallsignDbContext db, IClock clock)
        => new(db, new AirportRepository(db), new CargoJobSource(EconomyConfig.Default), clock, EconomyConfig.Default,
               new WorldOracle(EconomyConfig.Default));

    [Fact]
    public async Task Refresh_GeneratesNearbyCargoJobs_AndPersists()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);

        int count;
        using (var db = tdb.NewContext())
            count = await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, count: 8, seed: 1);
        Assert.True(count > 0);

        using (var db = tdb.NewContext())
        {
            var jobs = await Board(db, clock).GetAvailableAsync("EHAM");
            Assert.NotEmpty(jobs);
            Assert.All(jobs, j =>
            {
                Assert.Equal(MissionType.Cargo, j.Type);
                Assert.Equal("EHAM", j.OriginIcao);
                Assert.NotEqual("EHAM", j.DestIcao);
                Assert.NotEqual("EHHELI", j.DestIcao); // heliport is not landable
                Assert.NotEqual("NL-0099", j.DestIcao); // no ICAO code -> not a real destination
                Assert.True(j.RewardCents > 0);
                Assert.True(j.Xp > 0);
                Assert.True(j.WeightLbs is >= 200 and <= 3000);
                Assert.InRange(j.DistanceNm, 20, 400);
                Assert.True(j.ExpiresAt > clock.UtcNow);
                // Reward = base rate × the macro-economy demand multiplier × the seasonal tilt (Phase 12), frozen at posting.
                double demand = new WorldOracle(EconomyConfig.Default).EconomyPhaseAt(clock.UtcNow).DemandMult
                    * EconomyConfig.Default.SeasonalDemandFactor(GameCalendar.Season(clock.UtcNow, 52.3086));
                long expected = (long)Math.Round(EconomyConfig.Default.CargoRewardCents(j.DistanceNm, j.WeightLbs) * demand);
                Assert.Equal(expected, j.RewardCents);
            });
        }
    }

    [Fact]
    public async Task Refresh_Twice_ReplacesJobs_DoesNotAccumulate()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);
        using (var db = tdb.NewContext()) await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, 6, 1);
        using (var db = tdb.NewContext()) await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, 6, 2);

        using (var db = tdb.NewContext())
            Assert.Equal(6, (await Board(db, clock).GetAvailableAsync("EHAM")).Count);
    }

    [Fact]
    public async Task GetAvailable_ExcludesExpiredOffers()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using (var db = tdb.NewContext()) await SeedAirportsAsync(db);
        using (var db = tdb.NewContext()) await Board(db, clock).RefreshAsync("EHAM", PilotRank.Trainee, 5, 1);

        clock.UtcNow = clock.UtcNow.AddHours(EconomyConfig.Default.JobOfferHours + 1);
        using (var db = tdb.NewContext())
            Assert.Empty(await Board(db, clock).GetAvailableAsync("EHAM"));
    }

    [Fact]
    public void SeasonalDemandFactor_TiltsTheYear_BoundedAndHemisphereCorrect()
    {
        var c = EconomyConfig.Default;
        Assert.True(c.SeasonalDemandFactor("Winter") > 1.0);  // heating/holiday freight pays more
        Assert.True(c.SeasonalDemandFactor("Summer") < 1.0);  // the quiet season
        Assert.Equal(1.0, c.SeasonalDemandFactor("Spring"));  // neutral
        foreach (var s in new[] { "Winter", "Summer", "Autumn", "Spring" })
            Assert.InRange(c.SeasonalDemandFactor(s), 0.9, 1.1); // bounded — a gentle colour, never a balance swing
        // Hemisphere-correct: the same January instant is Winter up north, Summer down south.
        var jan = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Winter", GameCalendar.Season(jan, 52));
        Assert.Equal("Summer", GameCalendar.Season(jan, -33));
    }
}
