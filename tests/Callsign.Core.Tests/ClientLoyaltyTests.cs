using Callsign.Core.Aircraft;
using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

/// <summary>Phase 8d — named clients and the loyalty they accrue (and spend as a repeat premium).</summary>
public class ClientLoyaltyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ── the deterministic roster ────────────────────────────────────────────────────────────────

    [Fact]
    public void Roster_IsDeterministic_AndStableSized()
    {
        var a = ClientRoster.Roster("EHAM", "Amsterdam Schiphol");
        var b = ClientRoster.Roster("EHAM", "Amsterdam Schiphol");
        Assert.Equal(ClientRoster.DefaultSize, a.Count);
        Assert.Equal(a.Select(s => s.Key), b.Select(s => s.Key));   // same field -> same roster
        Assert.Equal(a.Select(s => s.Name), b.Select(s => s.Name));
        Assert.All(a, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.Equal(a.Select(s => s.Key).Distinct().Count(), a.Count); // slot keys are unique
    }

    [Fact]
    public void Pick_IsDeterministic_AndDrawsFromTheRoster()
    {
        var rosterKeys = ClientRoster.Roster("EHRD", "Rotterdam").Select(s => s.Key).ToHashSet();
        for (int i = 0; i < 20; i++)
        {
            var p1 = ClientRoster.Pick("EHRD", "Rotterdam", i);
            var p2 = ClientRoster.Pick("EHRD", "Rotterdam", i);
            Assert.Equal(p1, p2);                       // same (origin, ordinal) -> same client
            Assert.Contains(p1.Key, rosterKeys);        // never invents a client outside the roster
        }
    }

    [Fact]
    public void DifferentFields_GetDifferentRosters()
    {
        var a = ClientRoster.Roster("EHAM", "Amsterdam").Select(s => s.Key);
        var b = ClientRoster.Roster("EGLL", "London").Select(s => s.Key);
        Assert.NotEqual(a, b);
    }

    // ── the config math ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoyaltyDelta_BuildsOnFull_SoursOnFailure()
    {
        var cfg = EconomyConfig.Default;
        Assert.True(cfg.ClientLoyaltyDeltaMilli(MissionGrade.Full, scored: false, 0) > 0);
        Assert.True(cfg.ClientLoyaltyDeltaMilli(MissionGrade.Partial, scored: false, 0) < 0);
        Assert.True(cfg.ClientLoyaltyDeltaMilli(MissionGrade.Failed, scored: false, 0) < 0);
        // A sharper flight builds a little more loyalty than a sloppy one.
        int hi = cfg.ClientLoyaltyDeltaMilli(MissionGrade.Full, scored: true, 100);
        int lo = cfg.ClientLoyaltyDeltaMilli(MissionGrade.Full, scored: true, 0);
        Assert.True(hi > lo);
    }

    [Fact]
    public void BonusPct_ZeroBelowThreshold_RampsToCap()
    {
        var cfg = EconomyConfig.Default;
        Assert.Equal(0, cfg.ClientLoyaltyBonusPct(0));
        Assert.Equal(0, cfg.ClientLoyaltyBonusPct(cfg.ClientLoyaltyBonusThresholdMilli));
        Assert.True(cfg.ClientLoyaltyBonusPct(cfg.ClientLoyaltyBonusThresholdMilli + 1) > 0);
        Assert.Equal(cfg.ClientLoyaltyBonusMaxPct, cfg.ClientLoyaltyBonusPct(EconomyConfig.LoyaltyMax), 6);
        // monotonic in loyalty
        Assert.True(cfg.ClientLoyaltyBonusPct(80_000) > cfg.ClientLoyaltyBonusPct(40_000));
    }

    // ── the settlement wiring ───────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid CompanyId, Guid PilotId, Guid JobId);

    private static async Task<Seed> SeedAsync(CallsignDbContext db, string? clientKey = "EHAM#1",
        string? clientName = "Delta Cargo Partners", MissionType type = MissionType.Cargo, long reward = 200_000)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Test Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        var acType = new AircraftType
        {
            Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172 Skyhawk",
            Category = AircraftCategory.LightSingle, UsefulLoadLbs = 878,
            Aliases = [new AircraftTitleAlias { Title = "Cessna 172 Skyhawk", TitleNormalized = AircraftTitle.Normalize("Cessna 172 Skyhawk") }],
        };
        var job = new Job
        {
            Id = Guid.NewGuid(), Type = type, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Machine parts",
            WeightLbs = 500, RewardCents = reward, Xp = 20, DistanceNm = 120, RequiredRank = PilotRank.Trainee,
            ClientKey = clientKey, ClientName = clientName, GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
        };
        db.Companies.Add(company); db.Pilots.Add(pilot); db.AircraftTypes.Add(acType); db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return new Seed(company.Id, pilot.Id, job.Id);
    }

    private static FlightRecord Flown(double fpm, string title = "Cessna 172 Skyhawk")
        => new(title, T0.AddMinutes(5), T0.AddMinutes(55), fpm, 9000, 52.3, 4.76, 51.95, 4.44, 120, 60, []);

    private static SettlementService Settlement(CallsignDbContext db, IClock clock)
        => new(db, new LedgerService(db, clock), clock, EconomyConfig.Default);

    [Fact]
    public async Task FreshClient_FullDelivery_CreatesRow_BuildsLoyalty_PaysNoPremium()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid asg;
        using (var db = tdb.NewContext()) { var s = await SeedAsync(db); asg = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id; }

        SettlementResult r;
        using (var db = tdb.NewContext()) r = await Settlement(db, clock).SettleAsync(asg, Flown(-80));

        using (var db = tdb.NewContext())
        {
            var client = await db.Clients.SingleAsync();
            Assert.Equal("EHAM#1", client.ClientKey);
            Assert.Equal(EconomyConfig.Default.ClientLoyaltyFullMilli, client.LoyaltyMilli); // unscored full = flat gain
            Assert.Equal(1, client.JobsCompleted);
            Assert.Equal(0, client.JobsFailed);
        }
        // Loyalty was 0 coming in -> no repeat premium on this first job.
        Assert.DoesNotContain(r.Breakdown.Lines, l => l.Label.Contains("Loyal client"));
    }

    [Fact]
    public async Task LoyalClient_PaysARepeatPremium_OnTopOfTheReward()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        var cfg = EconomyConfig.Default;
        Guid asg; Guid company;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            company = s.CompanyId;
            // A client we've already built a bond with.
            db.Clients.Add(new Client
            {
                Id = Guid.NewGuid(), CompanyId = s.CompanyId, ClientKey = "EHAM#1", Name = "Delta Cargo Partners",
                HomeIcao = "EHAM", LoyaltyMilli = 60_000, JobsCompleted = 12, FirstSeenAt = T0, UpdatedAt = T0,
            });
            await db.SaveChangesAsync();
            asg = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        SettlementResult r;
        using (var db = tdb.NewContext()) r = await Settlement(db, clock).SettleAsync(asg, Flown(-80));

        long expectedPremium = (long)Math.Round(200_000 * (decimal)cfg.ClientLoyaltyBonusPct(60_000), MidpointRounding.AwayFromZero);
        Assert.True(expectedPremium > 0);
        var line = Assert.Single(r.Breakdown.Lines, l => l.Label.Contains("Loyal client"));
        Assert.Equal(expectedPremium, line.AmountCents);
        // The premium is a real cash line — the payout is base + landing + premium (fees waived, no airport seeded).
        Assert.Equal(r.PayoutCents, r.Breakdown.Lines.Sum(l => l.AmountCents));

        using (var db = tdb.NewContext())
        {
            var client = await db.Clients.SingleAsync();
            Assert.True(client.LoyaltyMilli > 60_000); // the delivery grew the bond further
            Assert.Equal(13, client.JobsCompleted);
        }
    }

    [Fact]
    public async Task FailedDelivery_SoursLoyalty_AndPaysNoPremium()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid asg;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db, type: MissionType.Sensitive); // fragile freight
            db.Clients.Add(new Client
            {
                Id = Guid.NewGuid(), CompanyId = s.CompanyId, ClientKey = "EHAM#1", Name = "Delta Cargo Partners",
                HomeIcao = "EHAM", LoyaltyMilli = 40_000, JobsCompleted = 5, FirstSeenAt = T0, UpdatedAt = T0,
            });
            await db.SaveChangesAsync();
            asg = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        SettlementResult r;
        using (var db = tdb.NewContext()) r = await Settlement(db, clock).SettleAsync(asg, Flown(-900)); // a slam destroys fragile goods

        using (var db = tdb.NewContext())
        {
            var client = await db.Clients.SingleAsync();
            Assert.Equal(40_000 + EconomyConfig.Default.ClientLoyaltyFailedMilli, client.LoyaltyMilli); // soured
            Assert.Equal(1, client.JobsFailed);
            Assert.Equal(5, client.JobsCompleted); // a failure does not count as completed
        }
        Assert.DoesNotContain(r.Breakdown.Lines, l => l.Label.Contains("Loyal client")); // failed = no earned base = no premium
    }

    [Fact]
    public async Task AnonymousJob_NoClientKey_TouchesNoClient_AndPaysNoPremium()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid asg;
        using (var db = tdb.NewContext()) { var s = await SeedAsync(db, clientKey: null, clientName: null); asg = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id; }

        SettlementResult r;
        using (var db = tdb.NewContext()) r = await Settlement(db, clock).SettleAsync(asg, Flown(-80));

        using (var db = tdb.NewContext()) Assert.Empty(await db.Clients.ToListAsync()); // no relationship created
        Assert.DoesNotContain(r.Breakdown.Lines, l => l.Label.Contains("Loyal client"));
    }

    // ── the board assigns a client to every offer ───────────────────────────────────────────────

    [Fact]
    public async Task Board_AssignsAClientToEveryGeneratedJob()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(
                new Airport { Ident = "EHAM", IcaoCode = "EHAM", Name = "Amsterdam Schiphol", Latitude = 52.3086, Longitude = 4.7639, Kind = AirportKind.LargeAirport },
                new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = 51.9569, Longitude = 4.4372, Kind = AirportKind.LargeAirport },
                new Airport { Ident = "EHEH", IcaoCode = "EHEH", Name = "Eindhoven", Latitude = 51.4501, Longitude = 5.3745, Kind = AirportKind.LargeAirport });
            await db.SaveChangesAsync();
        }
        var cfg = EconomyConfig.Default;
        using (var db = tdb.NewContext())
        {
            var board = new JobBoardService(db, new AirportRepository(db), new CargoJobSource(cfg), clock, cfg, new WorldOracle(cfg));
            await board.RefreshAsync("EHAM", PilotRank.Trainee, count: 6, seed: 7);
        }
        using (var db = tdb.NewContext())
        {
            var jobs = await db.Jobs.ToListAsync();
            Assert.NotEmpty(jobs);
            Assert.All(jobs, j =>
            {
                Assert.False(string.IsNullOrWhiteSpace(j.ClientKey));
                Assert.False(string.IsNullOrWhiteSpace(j.ClientName));
                Assert.StartsWith("EHAM#", j.ClientKey);
            });
        }
    }
}
