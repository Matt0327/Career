using Callsign.Core.Challenges;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Rotating daily/weekly challenges (Phase 12): a short goal measured as the GROWTH of a shared metric over its
/// period, paying a one-off cash reward when the player CLAIMS a genuinely-met challenge — once.
/// </summary>
public class ChallengesServiceTests
{
    private static ChallengesService Svc(CallsignDbContext db, FakeClock clock)
        => new(db, clock,
            new ProgressMetricsService(db, new FinanceService(db, clock, EconomyConfig.Default)),
            new LedgerService(db, clock));

    private static (Company Company, Pilot Pilot) Seed(CallsignDbContext db)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        return (company, pilot);
    }

    private static void AddFlight(CallsignDbContext db, Guid pilotId, double fpm = -120) =>
        db.Flights.Add(new Callsign.Core.Domain.Flight
        {
            Id = Guid.NewGuid(),
            FlownByPilotId = pilotId,
            AircraftTitle = "Cessna 172",
            PayoutBreakdownJson = "[]",
            TouchdownFpm = fpm,
        });

    // Walk forward from a fixed seed date to the first day whose daily board carries the given challenge —
    // deterministic (ForPeriod is pure), so the test never depends on which challenges happen to rotate in.
    private static FakeClock ClockOnDayWith(string dailyKey)
    {
        var d = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 400; i++)
        {
            var pk = $"D:{d.UtcDateTime:yyyy-MM-dd}";
            if (ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, pk).Any(c => c.Key == dailyKey))
                return new FakeClock { UtcNow = d };
            d = d.AddDays(1);
        }
        throw new Xunit.Sdk.XunitException($"No day found carrying challenge {dailyKey}");
    }

    [Fact]
    public void Catalog_IsWellFormed_UniqueKeys_PositiveTargetsAndRewards()
    {
        var all = ChallengeCatalog.All;
        Assert.Equal(all.Count, all.Select(c => c.Key).Distinct().Count());
        Assert.Contains(all, c => c.Cadence == ChallengeCadence.Daily);
        Assert.Contains(all, c => c.Cadence == ChallengeCadence.Weekly);
        Assert.All(all, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Detail));
            Assert.True(c.Target > 0);
            Assert.True(c.RewardCents > 0);
        });
    }

    [Fact]
    public void ForPeriod_IsDeterministic_SizedRight_AndRotates()
    {
        // Same key → identical draw (learnable/shareable); the count matches the catalog constants.
        var a = ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, "D:2026-06-01");
        var b = ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, "D:2026-06-01");
        Assert.Equal(ChallengeCatalog.DailyCount, a.Count);
        Assert.Equal(a.Select(c => c.Key), b.Select(c => c.Key));
        Assert.Equal(ChallengeCatalog.WeeklyCount, ChallengeCatalog.ForPeriod(ChallengeCadence.Weekly, "W:2026-23").Count);

        // Different period keys don't all produce the identical board — the roll actually rotates.
        var boards = Enumerable.Range(1, 40)
            .Select(i => string.Join(",", ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, $"D:2026-06-{i:D2}").Select(c => c.Key)))
            .Distinct().Count();
        Assert.True(boards > 1);
    }

    [Fact]
    public async Task Progress_IsMeasuredFromBaseline_NotAbsolute()
    {
        using var tdb = new TestDb();
        var clock = ClockOnDayWith("d-legs-3");
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        for (int i = 0; i < 50; i++) AddFlight(db, pilot.Id); // a big career history BEFORE the period
        await db.SaveChangesAsync();

        // First read captures the baseline (Flights = 50), so a "fly 3 today" is at zero — not already done.
        var first = (await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id)).Single(c => c.Key == "d-legs-3");
        Assert.Equal(0, first.Progress);
        Assert.False(first.Done);

        AddFlight(db, pilot.Id); // one leg this period
        await db.SaveChangesAsync();
        var after = (await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id)).Single(c => c.Key == "d-legs-3");
        Assert.Equal(1, after.Progress); // delta from baseline, NOT the absolute 51
        Assert.False(after.Done);
    }

    [Fact]
    public async Task Claim_PaysRewardOnce_AndSecondClaimIsRejected()
    {
        using var tdb = new TestDb();
        var clock = ClockOnDayWith("d-legs-3");
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();

        await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id); // baseline = 0 flights
        for (int i = 0; i < 3; i++) AddFlight(db, pilot.Id);
        await db.SaveChangesAsync();

        var view = (await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id)).Single(c => c.Key == "d-legs-3");
        Assert.True(view.Done);
        Assert.False(view.Claimed);

        var result = await Svc(db, clock).ClaimAsync(company.Id, pilot.Id, "d-legs-3");
        Assert.True(result.Ok);
        Assert.Equal(8_000_00, result.PaidCents);

        var again = await Svc(db, clock).ClaimAsync(company.Id, pilot.Id, "d-legs-3");
        Assert.False(again.Ok);

        using var check = tdb.NewContext();
        Assert.Equal(8_000_00, (await check.Companies.FindAsync(company.Id))!.CashCents);
        Assert.Equal(1, await check.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.ChallengeReward));
    }

    [Fact]
    public async Task Claim_RejectedWhenNotDone()
    {
        using var tdb = new TestDb();
        var clock = ClockOnDayWith("d-legs-3");
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();

        await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id);
        AddFlight(db, pilot.Id); // only 1 of 3
        await db.SaveChangesAsync();

        var result = await Svc(db, clock).ClaimAsync(company.Id, pilot.Id, "d-legs-3");
        Assert.False(result.Ok);
        using var check = tdb.NewContext();
        Assert.Equal(0, await check.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.ChallengeReward));
    }

    [Fact]
    public async Task NewPeriod_CapturesFreshBaseline()
    {
        using var tdb = new TestDb();
        var clock = ClockOnDayWith("d-legs-3");
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();

        await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id); // day-1 baseline = 0
        for (int i = 0; i < 3; i++) AddFlight(db, pilot.Id);
        await db.SaveChangesAsync();
        Assert.True((await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id)).Single(c => c.Key == "d-legs-3").Done);

        // Advance to the next day whose board still carries the challenge: a fresh period → fresh baseline (Flights=3),
        // so yesterday's completion doesn't carry over.
        var day1 = clock.UtcNow;
        var next = day1.AddDays(1);
        for (int i = 0; i < 400; i++)
        {
            var pk = $"D:{next.UtcDateTime:yyyy-MM-dd}";
            if (ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, pk).Any(c => c.Key == "d-legs-3")) break;
            next = next.AddDays(1);
        }
        clock.UtcNow = next;

        var freshDay = (await Svc(db, clock).GetActiveAsync(company.Id, pilot.Id)).Single(c => c.Key == "d-legs-3");
        Assert.Equal(0, freshDay.Progress);
        Assert.False(freshDay.Done);
    }
}
