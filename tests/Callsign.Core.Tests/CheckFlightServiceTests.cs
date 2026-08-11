using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

public class CheckFlightServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static FlightRecord Flown(double fpm) => new("Test", T0, T0.AddMinutes(30), fpm, 3000, 52, 4, 52, 4, 20, 40, []);
    private static CheckFlightService Svc(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    private static async Task<(Guid CompanyId, Guid PilotId)> SeedAsync(TestDb tdb, IClock clock, long cashCents)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "P", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.AddRange(company, pilot);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return (company.Id, pilot.Id);
    }

    [Theory]
    [InlineData(-40, 5)]
    [InlineData(-100, 4)]
    [InlineData(-180, 3)]
    [InlineData(-250, 0)]
    public void Stars_Bands(double fpm, int expected) => Assert.Equal(expected, Cfg.CheckFlightStars(fpm));

    [Fact]
    public void Fee_RisesWithClass()
    {
        Assert.True(Cfg.CheckFlightFeeCents(QualClass.C) > Cfg.CheckFlightFeeCents(QualClass.A));
        Assert.Equal(200_000 + 2 * 300_000, Cfg.CheckFlightFeeCents(QualClass.C)); // A=0, B=1, C=2
    }

    [Fact]
    public async Task Attempt_Pass_AwardsClass_AndDebitsFee()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, pilotId) = await SeedAsync(tdb, clock, 100_000_000);
        long fee = Cfg.CheckFlightFeeCents(QualClass.C);

        CheckFlightResult r;
        using (var db = tdb.NewContext())
            r = await Svc(db, clock).AttemptAsync(companyId, pilotId, QualClass.C, Flown(-40)); // greaser

        Assert.True(r.Passed);
        Assert.Equal(5, r.Stars);
        using (var db = tdb.NewContext())
        {
            var q = await db.PilotQualifications.SingleAsync(x => x.PilotId == pilotId && x.Class == QualClass.C);
            Assert.Equal(5, q.Stars);
            Assert.Equal(40d, q.BestTouchdownFpm!.Value);
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CheckFlightFee));
            Assert.Equal(100_000_000 - fee, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }

    [Fact]
    public async Task Attempt_Fail_DebitsFee_ButAwardsNothing()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, pilotId) = await SeedAsync(tdb, clock, 100_000_000);
        long fee = Cfg.CheckFlightFeeCents(QualClass.C);

        CheckFlightResult r;
        using (var db = tdb.NewContext())
            r = await Svc(db, clock).AttemptAsync(companyId, pilotId, QualClass.C, Flown(-300)); // slammed

        Assert.False(r.Passed);
        using (var db = tdb.NewContext())
        {
            Assert.Empty(await db.PilotQualifications.Where(x => x.PilotId == pilotId && x.Class == QualClass.C).ToListAsync());
            Assert.Equal(100_000_000 - fee, (await db.Companies.FindAsync(companyId))!.CashCents); // still charged for the attempt
        }
    }

    [Fact]
    public async Task Attempt_Upgrades_ExistingClass_KeepingBestFpm()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, pilotId) = await SeedAsync(tdb, clock, 100_000_000);

        using (var db = tdb.NewContext())
            await Svc(db, clock).AttemptAsync(companyId, pilotId, QualClass.C, Flown(-180)); // 3★, best 180
        using (var db = tdb.NewContext())
            await Svc(db, clock).AttemptAsync(companyId, pilotId, QualClass.C, Flown(-40));  // 5★, best 40

        using (var db = tdb.NewContext())
        {
            var q = await db.PilotQualifications.SingleAsync(x => x.PilotId == pilotId && x.Class == QualClass.C);
            Assert.Equal(5, q.Stars);              // upgraded
            Assert.Equal(40d, q.BestTouchdownFpm!.Value); // best kept
        }
    }

    [Fact]
    public async Task Attempt_InsufficientCash_Throws_NoFeeNoQual()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, pilotId) = await SeedAsync(tdb, clock, 1_000); // $10

        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Svc(db, clock).AttemptAsync(companyId, pilotId, QualClass.C, Flown(-40)));
        using (var db = tdb.NewContext())
        {
            Assert.Empty(await db.PilotQualifications.ToListAsync());
            Assert.Equal(1_000, (await db.Companies.FindAsync(companyId))!.CashCents);
        }
    }
}
