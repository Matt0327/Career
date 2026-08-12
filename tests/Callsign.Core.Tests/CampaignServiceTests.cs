using Callsign.Core.Campaigns;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Campaigns (Phase 5b): sequential arcs that advance off the shared progress snapshot and pay a cash
/// reward through the ledger when the last step clears — once.
/// </summary>
public class CampaignServiceTests
{
    private static CampaignService Svc(CallsignDbContext db, FakeClock clock)
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

    private static void AddFlight(CallsignDbContext db, Guid pilotId, double fpm) =>
        db.Flights.Add(new Callsign.Core.Domain.Flight
        {
            Id = Guid.NewGuid(),
            FlownByPilotId = pilotId,
            AircraftTitle = "Cessna 172",
            PayoutBreakdownJson = "[]",
            TouchdownFpm = fpm,
        });

    private static void AddAircraft(CallsignDbContext db, Guid companyId)
    {
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
        db.AircraftTypes.Add(type);
        db.AircraftInstances.Add(new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = companyId, Tail = "CS-1", LocationIcao = "EHAM", Availability = AircraftAvailability.Available });
    }

    [Fact]
    public async Task Step_Advances_WhenItsObjectiveIsMet()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        AddFlight(db, pilot.Id, -120); // one settled flight clears "Wheels up"
        await db.SaveChangesAsync();

        var first = (await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id)).Single(c => c.Key == "first-contract");

        Assert.True(first.Steps[0].Done);  // Wheels up
        Assert.False(first.Steps[1].Done); // Your own wings — not yet
        Assert.Equal(1, first.StepIndex);
        Assert.False(first.Completed);
    }

    [Fact]
    public async Task CompletingTheArc_PaysTheRewardThroughTheLedger()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        AddAircraft(db, company.Id);
        AddFlight(db, pilot.Id, -45); // a flight AND a butter landing
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "seed"); // net worth → $100k

        var first = (await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id)).Single(c => c.Key == "first-contract");

        Assert.True(first.Completed);
        Assert.NotNull(first.CompletedAt);
        Assert.All(first.Steps, s => Assert.True(s.Done));

        using var check = tdb.NewContext();
        Assert.Equal(100_000_00 + 25_000_00, (await check.Companies.FindAsync(company.Id))!.CashCents);
        Assert.Equal(1, await check.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CampaignReward));
    }

    [Fact]
    public async Task Reward_IsPaidOnce_AcrossRepeatedEvaluations()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        AddAircraft(db, company.Id);
        AddFlight(db, pilot.Id, -45);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "seed");

        await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id);
        await Svc(db, clock).EvaluateAsync(company.Id, pilot.Id); // a second look must not pay again

        using var check = tdb.NewContext();
        Assert.Equal(100_000_00 + 25_000_00, (await check.Companies.FindAsync(company.Id))!.CashCents);
        Assert.Equal(1, await check.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CampaignReward));
    }
}
