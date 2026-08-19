using System.Text.Json;
using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

public class SettlementServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Seed(Guid CompanyId, Guid PilotId, Guid JobId);

    private static async Task<Seed> SeedAsync(CallsignDbContext db, long rewardCents = 200_000, int xp = 20, int weight = 500,
        MissionType missionType = MissionType.Cargo)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Test Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        var type = new AircraftType
        {
            Id = Guid.NewGuid(),
            Key = "C172",
            CanonicalName = "Cessna 172 Skyhawk",
            Category = AircraftCategory.LightSingle,
            UsefulLoadLbs = 878,
            Aliases = [new AircraftTitleAlias { Title = "Cessna 172 Skyhawk", TitleNormalized = AircraftTitle.Normalize("Cessna 172 Skyhawk") }],
        };
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = missionType,
            OriginIcao = "EHAM",
            DestIcao = "EHRD",
            Commodity = "Machine parts",
            WeightLbs = weight,
            RewardCents = rewardCents,
            Xp = xp,
            DistanceNm = 120,
            RequiredRank = PilotRank.Trainee,
            GeneratedAt = T0,
            ExpiresAt = T0.AddHours(6),
        };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        db.AircraftTypes.Add(type);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return new Seed(company.Id, pilot.Id, job.Id);
    }

    private static FlightRecord Flown(double touchdownFpm, string title = "Cessna 172 Skyhawk")
        => new(title, T0.AddMinutes(5), T0.AddMinutes(55), touchdownFpm, 9000, 52.3, 4.76, 51.95, 4.44, 120, 60, []);

    [Fact]
    public async Task Accept_FreezesQuote_AndTakesJobOffBoard()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var s = await SeedAsync(db);

        var a = await new JobAssignmentService(db, new FakeClock()).AcceptAsync(s.JobId, s.CompanyId, s.PilotId);

        Assert.Equal(200_000, a.RewardQuoteCents);
        Assert.Equal(AssignmentStatus.Accepted, a.Status);
        Assert.Null(a.DeadlineAt); // a cargo job has no delivery clock
        Assert.Empty(await db.Jobs.ToListAsync()); // taken off the board; settlement can't read it
    }

    [Fact]
    public async Task Accept_Express_FreezesADeliveryDeadline()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, missionType: MissionType.Express);

        var a = await new JobAssignmentService(db, new FakeClock(), EconomyConfig.Default)
            .AcceptAsync(s.JobId, s.CompanyId, s.PilotId);

        Assert.NotNull(a.DeadlineAt);
        Assert.True(a.DeadlineAt > a.AcceptedAt); // the clock is ticking from the moment you accept
    }

    [Fact]
    public async Task Settle_Express_ArrivedLate_FailsOnTheClock()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        DateTimeOffset deadline;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db, missionType: MissionType.Express);
            var a = await new JobAssignmentService(db, clock, EconomyConfig.Default).AcceptAsync(s.JobId, s.CompanyId, s.PilotId);
            assignmentId = a.Id;
            deadline = a.DeadlineAt!.Value;
        }

        // Arrive a full hour past the frozen deadline — the parcel is refused.
        var record = Flown(-80) with { ArrivedAt = deadline.AddMinutes(60) };
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);
            Assert.Equal(0, r.PayoutCents);
        }
        using (var db = tdb.NewContext())
            Assert.Equal((int)MissionGrade.Failed, (await db.Flights.SingleAsync()).OutcomeGrade);
    }

    [Fact]
    public async Task Settle_ItemisesPayout_PostsLedgerAtomically_AwardsXp_PersistsFlight()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db);
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        SettlementResult result;
        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            result = await svc.SettleAsync(assignmentId, Flown(-80)); // greaser: +10%
        }

        Assert.Equal(220_000, result.PayoutCents); // 200000 + 10%
        Assert.True(result.PayloadMatched);
        Assert.Equal(30, result.XpAwarded);        // 20 + round(20 * 0.5)

        using (var db = tdb.NewContext())
        {
            var ledger = await db.LedgerEntries.Where(e => e.AccountId == s.CompanyId).ToListAsync();
            Assert.Equal(2, ledger.Count);                          // JobPayout + JobBonus
            Assert.Equal(220_000, ledger.Sum(e => e.AmountCents));  // Σ ledger == payout
            var company = await db.Companies.FindAsync(s.CompanyId);
            Assert.Equal(220_000, company!.CashCents);              // cache == ledger
            var pilot = await db.Pilots.FindAsync(s.PilotId);
            Assert.Equal(30, pilot!.Xp);

            var flight = await db.Flights.SingleAsync();
            Assert.Equal(220_000, flight.PayoutCents);
            Assert.Equal(-80, flight.TouchdownFpm);
            var breakdown = JsonSerializer.Deserialize<PayoutBreakdown>(flight.PayoutBreakdownJson)!;
            Assert.Equal(220_000, breakdown.TotalCents);
            Assert.Equal(220_000, breakdown.Lines.Sum(l => l.AmountCents)); // lines sum to total

            var assignment = await db.JobAssignments.FindAsync(assignmentId);
            Assert.Equal(AssignmentStatus.Settled, assignment!.Status);
        }
    }

    [Fact]
    public async Task Settle_HardLanding_AppliesPenalty_ThatSumsCorrectly()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, companyId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            companyId = s.CompanyId;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            var r = await svc.SettleAsync(assignmentId, Flown(-900)); // -15%
            Assert.Equal(170_000, r.PayoutCents);
        }

        using (var db = tdb.NewContext())
        {
            var ledger = await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync();
            Assert.Contains(ledger, e => e.Category == LedgerCategory.Penalty && e.AmountCents == -30_000);
            Assert.Equal(170_000, ledger.Sum(e => e.AmountCents));
        }
    }

    [Fact]
    public async Task Settle_Twice_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-150));

        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SettleAsync(assignmentId, Flown(-150)));
        }
    }

    [Fact]
    public async Task Settle_RoundsMoney_AwayFromZero_NotBankers()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db, rewardCents: 215_350); // $2153.50
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-900)); // -15% of 215350 = -32302.5 -> away-from-zero -32303
            Assert.Equal(215_350 - 32_303, r.PayoutCents); // 183047 (banker's rounding would wrongly give 183048)
        }
    }

    [Fact]
    public async Task Settle_MissingPilot_Throws_BeforeMovingMoney()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, companyId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            companyId = s.CompanyId;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        // The pilot vanishes before settlement (e.g. a corrupt save) — settlement must refuse, not half-pay.
        using (var db = tdb.NewContext())
        {
            db.Pilots.RemoveRange(await db.Pilots.ToListAsync());
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SettleAsync(assignmentId, Flown(-150)));
        }
        using (var db = tdb.NewContext())
            Assert.Empty(await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync()); // no money moved
    }

    // A passenger charter gates the "right aircraft" bonus on SEATS, not payload weight.
    private static async Task<Guid> SeedPaxSettleAsync(CallsignDbContext db, FakeClock clock, int seats, int pax)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Test Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        var type = new AircraftType
        {
            Id = Guid.NewGuid(),
            Key = "PC12",
            CanonicalName = "Pilatus PC-12",
            Category = AircraftCategory.Turboprop,
            Seats = seats,
            UsefulLoadLbs = 50, // deliberately tiny: far below the passengers' weight, so only SEATS can match
            Aliases = [new AircraftTitleAlias { Title = "Pilatus PC-12", TitleNormalized = AircraftTitle.Normalize("Pilatus PC-12") }],
        };
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = MissionType.Passenger,
            OriginIcao = "EHAM",
            DestIcao = "EHRD",
            Commodity = "Corporate group",
            WeightLbs = pax * EconomyConfig.Default.PaxWeightLbs,
            Pax = pax,
            RewardCents = 200_000,
            Xp = 20,
            DistanceNm = 120,
            RequiredRank = PilotRank.Trainee,
            GeneratedAt = T0,
            ExpiresAt = T0.AddHours(6),
        };
        db.AddRange(company, pilot, type, job);
        await db.SaveChangesAsync();
        return (await new JobAssignmentService(db, clock).AcceptAsync(job.Id, company.Id, pilot.Id)).Id;
    }

    [Fact]
    public async Task Settle_PassengerJob_EnoughSeats_AwardsBonus_EvenWithTinyUsefulLoad()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
            assignmentId = await SeedPaxSettleAsync(db, clock, seats: 9, pax: 4);

        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            var r = await svc.SettleAsync(assignmentId,
                new FlightRecord("Pilatus PC-12", T0.AddMinutes(5), T0.AddMinutes(55), -80, 9000, 52.3, 4.76, 51.95, 4.44, 120, 200, []));
            Assert.True(r.PayloadMatched);     // 9 seats >= 4 pax (useful load of 50 lb is irrelevant for passengers)
            Assert.Equal(30, r.XpAwarded);     // 20 + round(20 * 0.5)
        }
    }

    [Fact]
    public async Task Settle_PassengerJob_TooFewSeats_NoBonus()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
            assignmentId = await SeedPaxSettleAsync(db, clock, seats: 2, pax: 4);

        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            var r = await svc.SettleAsync(assignmentId,
                new FlightRecord("Pilatus PC-12", T0.AddMinutes(5), T0.AddMinutes(55), -80, 9000, 52.3, 4.76, 51.95, 4.44, 120, 200, []));
            Assert.False(r.PayloadMatched);    // 2 seats < 4 pax
            Assert.Equal(20, r.XpAwarded);     // base XP only, no capability bonus
        }
    }

    [Fact]
    public async Task Settle_MovesTradeGoods_WithThePilot_ToTheDestination()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId, lotId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db); // Cargo EHAM -> EHRD
            var lot = new InventoryLot
            {
                Id = Guid.NewGuid(), CompanyId = s.CompanyId, Good = "coffee", Quantity = 5,
                UnitCostCents = 9_000, LocationIcao = "EHAM", AcquiredAt = T0, UpdatedAt = T0,
            };
            db.InventoryLots.Add(lot);
            await db.SaveChangesAsync();
            lotId = lot.Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-120));

        using (var db = tdb.NewContext())
        {
            var lot = await db.InventoryLots.FindAsync(lotId);
            Assert.Equal("EHRD", lot!.LocationIcao); // flew with the pilot to the destination
            Assert.Equal(5, lot.Quantity);           // untouched otherwise
        }
    }

    [Fact]
    public async Task Settle_CrossingXpThreshold_PromotesRank()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, pilotId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var pilot = new Pilot
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM",
                Xp = 490, Rank = PilotRank.Trainee, // one good leg away from Copilot (500)
            };
            var job = new Job
            {
                Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD",
                Commodity = "Machine parts", WeightLbs = 100, RewardCents = 200_000, Xp = 20, DistanceNm = 120,
                RequiredRank = PilotRank.Trainee, GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
            };
            db.AddRange(company, pilot, job);
            await db.SaveChangesAsync();
            pilotId = pilot.Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(job.Id, company.Id, pilot.Id)).Id;
        }

        SettlementResult r;
        using (var db = tdb.NewContext())
            r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-100));

        Assert.Equal(PilotRank.Copilot, r.PromotedTo);   // 490 + 20 XP crosses 500
        using (var db = tdb.NewContext())
            Assert.Equal(PilotRank.Copilot, (await db.Pilots.FindAsync(pilotId))!.Rank);
    }

    [Fact]
    public async Task Settle_BelowThreshold_DoesNotPromote()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db); // fresh pilot at 0 XP; one leg awards ~30
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-80));
            Assert.Null(r.PromotedTo); // still a Trainee
        }
    }

    [Fact]
    public async Task Settle_MovesReputation_AndLogsAnEvent()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db); // a Cargo job
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-100));

        int rep = MissionCatalog.Def(MissionType.Cargo).ReputationMilliReward;
        using (var db = tdb.NewContext())
        {
            Assert.Equal(rep, (await db.Pilots.FindAsync(s.PilotId))!.ReputationMilli); // gained the mission's reward
            var ev = await db.ReputationEvents.SingleAsync(e => e.PilotId == s.PilotId);
            Assert.Equal(rep, ev.DeltaMilli);
            Assert.Equal(rep, ev.BalanceMilli);
            Assert.Contains("Cargo", ev.Reason);
        }
    }

    [Fact]
    public async Task Settle_Illicit_CostsReputation()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, pilotId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            var pilot = new Pilot
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, Name = "A", HomeIcao = "EHAM", CurrentIcao = "EHAM",
                Rank = PilotRank.Captain, ReputationMilli = 5_000,
            };
            var job = new Job
            {
                Id = Guid.NewGuid(), Type = MissionType.Illicit, OriginIcao = "EHAM", DestIcao = "EHRD",
                Commodity = "Unmarked crates", WeightLbs = 500, RewardCents = 200_000, Xp = 20, DistanceNm = 120,
                RequiredRank = PilotRank.Copilot, GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
            };
            db.AddRange(company, pilot, job);
            await db.SaveChangesAsync();
            pilotId = pilot.Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(job.Id, company.Id, pilot.Id)).Id;
        }
        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-100));

        int rep = MissionCatalog.Def(MissionType.Illicit).ReputationMilliReward; // negative
        using (var db = tdb.NewContext())
            Assert.Equal(5_000 + rep, (await db.Pilots.FindAsync(pilotId))!.ReputationMilli); // reputation dropped
    }

    [Fact]
    public async Task Settle_PersistsTheTrackerEvents_InOrder_ForLogbookReplay()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        // The real scored moments the tracker produces during the leg (Phase 7a) — they must survive
        // settlement so the logbook can replay the flight instead of the client fabricating one.
        var events = new List<Callsign.Core.Flight.FlightEvent>
        {
            new(T0.AddMinutes(5), Callsign.Core.Flight.FlightEventSeverity.Info, "Takeoff"),
            new(T0.AddMinutes(54), Callsign.Core.Flight.FlightEventSeverity.Warning, "Taxi speed 42 kt exceeds 30 kt"),
            new(T0.AddMinutes(55), Callsign.Core.Flight.FlightEventSeverity.Success, "Landed at -80 fpm"),
        };
        var record = new FlightRecord("Cessna 172 Skyhawk", T0.AddMinutes(5), T0.AddMinutes(55),
            -80, 9000, 52.3, 4.76, 51.95, 4.44, 120, 60, events);

        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);

        using (var db = tdb.NewContext())
        {
            var flightId = (await db.Flights.SingleAsync()).Id;
            var persisted = await db.FlightEvents.Where(e => e.FlightId == flightId).OrderBy(e => e.Seq).ToListAsync();
            Assert.Equal(3, persisted.Count);
            Assert.Equal(new[] { "Takeoff", "Taxi speed 42 kt exceeds 30 kt", "Landed at -80 fpm" }, persisted.Select(e => e.Message));
            Assert.Equal(new[] { "Info", "Warning", "Success" }, persisted.Select(e => e.Severity)); // enum name preserved
            Assert.Equal(new[] { 0, 1, 2 }, persisted.Select(e => e.Seq));                            // stable chronological order
        }
    }

    [Fact]
    public async Task Settle_PersistsTheFlightScore_ForTheLogbook()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        // The tracker's Phase 7b assessment rides on the FlightRecord and must be stored on the Flight row.
        var record = Flown(-80) with
        {
            Scored = true, OverallScore = 82, LandingScore = 90, ApproachScore = 75,
            TouchdownG = 1.35, StabilizedApproach = true, ViolationPoints = 0,
        };
        using (var db = tdb.NewContext())
            await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);

        using (var db = tdb.NewContext())
        {
            var flight = await db.Flights.SingleAsync();
            Assert.Equal(82, flight.OverallScore);
            Assert.Equal(90, flight.LandingScore);
            Assert.Equal(75, flight.ApproachScore);
            Assert.Equal(1.35, flight.TouchdownG);
            Assert.True(flight.StabilizedApproach);
        }
    }

    [Fact]
    public async Task Settle_ScoredLeg_LeversOnTheScore_NotRawFpm()
    {
        // Two identical greaser touchdowns (-40 fpm), but one was a clean flight (96) and one was flown
        // dangerously (30). Under Phase 7c the payout tracks the WHOLE-flight score, not the touchdown.
        static async Task<long> SettleWithScore(int score)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            Guid assignmentId;
            using (var db = tdb.NewContext())
            {
                var s = await SeedAsync(db);
                assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
            }
            var record = Flown(-40) with { Scored = true, OverallScore = score, ScoreValid = true };
            using var db2 = tdb.NewContext();
            var r = await new SettlementService(db2, new LedgerService(db2, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);
            return r.PayoutCents;
        }

        Assert.Equal(230_000, await SettleWithScore(96)); // +15% for a 96 despite the identical fpm
        Assert.Equal(170_000, await SettleWithScore(30)); // -15% for a 30, same touchdown
    }

    [Fact]
    public async Task Settle_InvalidatedLeg_ForfeitsTheBonus_ButStillPays()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, companyId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db);
            companyId = s.CompanyId;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        // A perfect score, but flight-integrity monitoring caught a cheat — the bonus is voided.
        var record = Flown(-40) with { Scored = true, OverallScore = 96, ScoreValid = false };
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);
            Assert.Equal(200_000, r.PayoutCents); // base only — no performance bonus
        }
        using (var db = tdb.NewContext())
        {
            var ledger = await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync();
            Assert.DoesNotContain(ledger, e => e.Category == LedgerCategory.JobBonus); // bonus forfeited
            var flight = await db.Flights.SingleAsync();
            Assert.False(flight.ScoreValid);
        }
    }

    [Fact]
    public async Task Settle_SensitiveCargo_FirmArrival_PaysPartial_AndRecordsTheGrade()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db, missionType: MissionType.Sensitive);
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        // A firm -450 fpm arrival damages fragile freight to 80% (score 60 → no performance delta, so the
        // payout shows the mission grade cleanly): 200000 × 0.80 = 160000.
        var record = Flown(-40) with
        {
            Scored = true, OverallScore = 60, ScoreValid = true, TouchdownFpmWorst3 = -450, TouchdownG = 1.2,
        };
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);
            Assert.Equal(160_000, r.PayoutCents);
        }
        using (var db = tdb.NewContext())
        {
            var flight = await db.Flights.SingleAsync();
            Assert.Equal((int)MissionGrade.Partial, flight.OutcomeGrade);
            Assert.Contains("damaged", flight.OutcomeReason);
        }
    }

    [Fact]
    public async Task Settle_SensitiveCargo_Slam_Fails_NoBasePayout()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid assignmentId, companyId;
        using (var db = tdb.NewContext())
        {
            var s = await SeedAsync(db, missionType: MissionType.Sensitive);
            companyId = s.CompanyId;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        // A -1000 fpm slam destroys the load — the delivery fails and pays no base reward.
        var record = Flown(-40) with { Scored = true, OverallScore = 60, ScoreValid = true, TouchdownFpmWorst3 = -1000, TouchdownG = 1.5 };
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, record);
            Assert.Equal(0, r.PayoutCents);
        }
        using (var db = tdb.NewContext())
        {
            var ledger = await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync();
            Assert.DoesNotContain(ledger, e => e.Category == LedgerCategory.JobPayout); // no base paid
            var flight = await db.Flights.SingleAsync();
            Assert.Equal((int)MissionGrade.Failed, flight.OutcomeGrade);
            Assert.Equal(AssignmentStatus.Settled, (await db.JobAssignments.FindAsync(assignmentId))!.Status); // still closes
        }
    }

    [Fact]
    public async Task Settle_OwnedAircraft_ChargesForTheFuelBurned()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId, aircraftId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db);
            var typeId = await db.AircraftTypes.Select(t => t.Id).FirstAsync();
            var inst = new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = typeId, CompanyId = s.CompanyId, Tail = "CS-1",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM",
            };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            aircraftId = inst.Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        using (var db = tdb.NewContext())
        {
            // Flown(-80) burned 60 lb; greaser → +10% landing. 200000 + 20000 - (60 × $0.90 = 5400) = 214600.
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-80), aircraftId);
            Assert.Equal(214_600, r.PayoutCents);
        }
        using (var db = tdb.NewContext())
        {
            var ledger = await db.LedgerEntries.Where(e => e.AccountId == s.CompanyId).ToListAsync();
            Assert.Contains(ledger, e => e.Category == LedgerCategory.Fuel && e.AmountCents == -5_400);
            Assert.Equal(214_600, (await db.Companies.FindAsync(s.CompanyId))!.CashCents); // ledger sum == payout
        }
    }

    [Fact]
    public async Task Settle_OwnedAircraft_FuelFarmAtOrigin_DiscountsTheFuel()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId, aircraftId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db);
            var typeId = await db.AircraftTypes.Select(t => t.Id).FirstAsync();
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = typeId, CompanyId = s.CompanyId, Tail = "CS-1",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM",
            });
            // A level-3 fuel farm at the departure base (EHAM) → 25% off this leg's fuel.
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = s.CompanyId, AirportIcao = "EHAM", IsActive = true, FuelFarmLevel = 3 });
            await db.SaveChangesAsync();
            aircraftId = (await db.AircraftInstances.FirstAsync(a => a.CompanyId == s.CompanyId)).Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }

        using (var db = tdb.NewContext())
        {
            // 60 lb × $0.90 = 5400 gross, ×0.75 (25% off) = 4050 → payout 200000 + 20000 − 4050 = 215950.
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-80), aircraftId);
            Assert.Equal(215_950, r.PayoutCents);
        }
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.Where(e => e.AccountId == s.CompanyId).ToListAsync(),
                e => e.Category == LedgerCategory.Fuel && e.AmountCents == -4_050);
    }

    [Fact]
    public async Task Settle_OwnedAircraft_BaseWithoutFuelFarm_ChargesFullFuel()
    {
        // A base at the origin but with NO fuel farm built (level 0) charges full fuel — the facility, not the
        // base, is what discounts. (Distinct from the no-base path the other fuel test covers.)
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Seed s;
        Guid assignmentId, aircraftId;
        using (var db = tdb.NewContext())
        {
            s = await SeedAsync(db);
            var typeId = await db.AircraftTypes.Select(t => t.Id).FirstAsync();
            db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = typeId, CompanyId = s.CompanyId, Tail = "CS-1",
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM",
            });
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = s.CompanyId, AirportIcao = "EHAM", IsActive = true, FuelFarmLevel = 0 });
            await db.SaveChangesAsync();
            aircraftId = (await db.AircraftInstances.FirstAsync(a => a.CompanyId == s.CompanyId)).Id;
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, s.PilotId)).Id;
        }
        using (var db = tdb.NewContext())
        {
            var r = await new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default)
                .SettleAsync(assignmentId, Flown(-80), aircraftId);
            Assert.Equal(214_600, r.PayoutCents); // full fuel −5400, as with no base at all
        }
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.Where(e => e.AccountId == s.CompanyId).ToListAsync(),
                e => e.Category == LedgerCategory.Fuel && e.AmountCents == -5_400);
    }

    [Theory]
    [InlineData(-50, 0.10)]
    [InlineData(-150, 0.05)]
    [InlineData(-300, 0.00)]
    [InlineData(-500, -0.05)]
    [InlineData(-900, -0.15)]
    [InlineData(-1500, -0.30)]
    public void LandingModifier_Bands(double fpm, double expected)
        => Assert.Equal(expected, (double)EconomyConfig.Default.LandingModifierPct(fpm), 3);
}
