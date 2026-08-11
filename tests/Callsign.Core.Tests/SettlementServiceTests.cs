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

    private static async Task<Seed> SeedAsync(CallsignDbContext db, long rewardCents = 200_000, int xp = 20, int weight = 500)
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
            Type = MissionType.Cargo,
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
        Assert.Empty(await db.Jobs.ToListAsync()); // taken off the board; settlement can't read it
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
            assignmentId = (await new JobAssignmentService(db, clock).AcceptAsync(s.JobId, s.CompanyId, Guid.NewGuid())).Id;
        }
        using (var db = tdb.NewContext())
        {
            var svc = new SettlementService(db, new LedgerService(db, clock), clock, EconomyConfig.Default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SettleAsync(assignmentId, Flown(-150)));
        }
        using (var db = tdb.NewContext())
            Assert.Empty(await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync()); // no money moved
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
