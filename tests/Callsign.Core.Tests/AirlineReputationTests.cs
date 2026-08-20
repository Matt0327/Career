using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

/// <summary>
/// Phase 11a — the airline's OPERATING reputation engine, across both seams: a player leg (by score) and the
/// autonomous crew loop (toward crew competence), plus the pure config helpers and the persisted defaults.
/// Money-neutral: this engine writes no ledger row.
/// </summary>
public class AirlineReputationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    [Fact]
    public async Task RepLog_TagsPlayerVsCrewSource()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId, pilotId, jobId, staffId, aircraftId;

        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
            db.Companies.Add(company);
            db.Pilots.Add(pilot);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType
            {
                Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172 Skyhawk",
                Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900,
                Aliases = [new AircraftTitleAlias { Title = "Cessna 172 Skyhawk", TitleNormalized = AircraftTitle.Normalize("Cessna 172 Skyhawk") }],
            };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Crew", SkillMilli = 60_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = T0, LastPaidAt = T0 };
            db.Staff.Add(staff);
            var job = new Job
            {
                Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD",
                Commodity = "Machine parts", WeightLbs = 100, RewardCents = 200_000, Xp = 20, DistanceNm = 120,
                RequiredRank = PilotRank.Trainee, GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id; pilotId = pilot.Id; jobId = job.Id; staffId = staff.Id; aircraftId = inst.Id;
        }

        // A player leg you flew yourself → a Player-tagged move.
        using (var db = tdb.NewContext())
        {
            var assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(jobId, companyId, pilotId)).Id;
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default).SettleAsync(
                assignmentId,
                new FlightRecord("Cessna 172 Skyhawk", T0.AddMinutes(5), T0.AddMinutes(55), -40, 9000, 52.3, 4.76, 51.95, 4.44, 120, 60, [])
                    with { Scored = true, OverallScore = 96, ScoreValid = true });
        }

        // A second tail flying an autonomous line → a Crew-tagged move.
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, EconomyConfig.Default).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            var events = await db.AirlineReputationEvents.Where(e => e.CompanyId == companyId).ToListAsync();
            Assert.Single(events, e => e.Source == AirlineRepSource.Player); // exactly one from your flying
            Assert.Single(events, e => e.Source == AirlineRepSource.Crew);   // exactly one from your crew
        }
    }

    [Fact]
    public async Task FreshCompany_StartsAtZeroOperatingRep_AndEventsDefaultToPlayer()
    {
        using var tdb = new TestDb();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" }; // OperatingReputationMilli not set
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;
        }
        using (var db = tdb.NewContext())
        {
            Assert.Equal(0, (await db.Companies.FindAsync(companyId))!.OperatingReputationMilli); // an unearned name

            db.AirlineReputationEvents.Add(new AirlineReputationEvent
            {
                CompanyId = companyId, DeltaMilli = 10, BalanceMilli = 10, Reason = "seed", At = T0,
            }); // Source not set
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
            Assert.Equal(AirlineRepSource.Player, (await db.AirlineReputationEvents.SingleAsync()).Source); // defaults to Player
    }

    [Fact]
    public void OperatingRepPlayerDelta_IsPureAcrossEveryBranch()
    {
        var c = EconomyConfig.Default;
        Assert.Equal(0, c.OperatingRepPlayerDeltaMilli(96, scored: false, valid: false));   // unscored → no judgement (L10)
        Assert.Equal(c.OperatingRepPlayerCheatMilli, c.OperatingRepPlayerDeltaMilli(96, true, false)); // cheated → worst move
        Assert.Equal(c.OperatingRepPlayerGainMilli, c.OperatingRepPlayerDeltaMilli(90, true, true));   // at the high gate → gain
        Assert.Equal(0, c.OperatingRepPlayerDeltaMilli(89, true, true));                    // just below high → coaching band (L9)
        Assert.Equal(0, c.OperatingRepPlayerDeltaMilli(40, true, true));                    // at the low gate → still neutral
        Assert.Equal(c.OperatingRepPlayerDingMilli, c.OperatingRepPlayerDeltaMilli(39, true, true));   // below low → ding
        Assert.True(c.OperatingRepPlayerCheatMilli < c.OperatingRepPlayerDingMilli);        // a cheat costs more than an honest poor leg
    }

    [Fact]
    public void OperatingRepConvergeFrac_ProportionalToTrips_SaturatesAtOne()
    {
        var c = EconomyConfig.Default;
        Assert.Equal(0.0, c.OperatingRepConvergeFrac(0));            // no trips → no convergence
        Assert.Equal(0.04, c.OperatingRepConvergeFrac(1), 6);       // one trip closes 4% of the gap
        Assert.Equal(1.0, c.OperatingRepConvergeFrac(25), 6);       // saturates exactly at 25 trips
        Assert.Equal(1.0, c.OperatingRepConvergeFrac(1000), 6);     // never more than the whole gap
    }

    [Fact]
    public void OperatingRepCrewPull_ConvergesTowardCrew_AndCapsFraction()
    {
        var c = EconomyConfig.Default;
        Assert.Equal(0, c.OperatingRepCrewPullMilli(20_000, 60_000, 0));   // no trips → no pull
        Assert.True(c.OperatingRepCrewPullMilli(20_000, 60_000, 1) > 0);   // sharper crew than the name → up
        Assert.True(c.OperatingRepCrewPullMilli(80_000, 45_000, 1) < 0);   // greener crew than the name → down (L12)
        Assert.Equal(4_000, c.OperatingRepCrewPullMilli(0, 100_000, 1));   // one trip closes 4% of the gap
        Assert.Equal(100_000 - 20_000, c.OperatingRepCrewPullMilli(20_000, 100_000, 100)); // frac caps at 1.0 → closes the whole gap, never overshoots
    }
}
