using Callsign.Core.Airline;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Airline identity + standing (Phase 5c): a look with derived defaults + validation, and a computed
/// "reputation at scale" tier that reads the operation, never stored.
/// </summary>
public class AirlineServiceTests
{
    private static AirlineService Svc(CallsignDbContext db)
    {
        var clock = new FakeClock();
        return new AirlineService(db, new ProgressMetricsService(db, new FinanceService(db, clock, EconomyConfig.Default)));
    }

    private static (Company Company, Pilot Pilot) Seed(CallsignDbContext db, string name = "SkyBridge Air")
    {
        var company = new Company { Id = Guid.NewGuid(), Name = name };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        return (company, pilot);
    }

    [Fact]
    public async Task Identity_DerivesADefaultLook_WhenUncustomised()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, _) = Seed(db, "SkyBridge Air");
        await db.SaveChangesAsync();

        var id = await Svc(db).GetIdentityAsync(company.Id);

        Assert.Equal("SkyBridge Air", id.Name);
        Assert.Equal("SA", id.TailCode);            // initials of the two words
        Assert.False(id.Customised);
        Assert.Matches("^#[0-9a-fA-F]{6}$", id.AccentColorHex);
        Assert.Contains(id.EmblemKey, AirlineEmblems.All);
    }

    [Fact]
    public async Task SetIdentity_PersistsAndNormalises()
    {
        using var tdb = new TestDb();
        var (companyId, _) = await SeedSavedAsync(tdb);

        using (var db = tdb.NewContext())
        {
            var id = await Svc(db).SetIdentityAsync(companyId, "  Test Air ", "tst", "#123456", "delta");
            Assert.Equal("Test Air", id.Name);      // trimmed
            Assert.Equal("TST", id.TailCode);       // upper-cased
            Assert.Equal("delta", id.EmblemKey);
            Assert.True(id.Customised);
        }
        using (var db = tdb.NewContext())
            Assert.Equal("Test Air", (await Svc(db).GetIdentityAsync(companyId)).Name); // persisted
    }

    [Fact]
    public async Task SetIdentity_RejectsBadInput()
    {
        using var tdb = new TestDb();
        var (companyId, _) = await SeedSavedAsync(tdb);
        using var db = tdb.NewContext();
        var svc = Svc(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "", "TST", "#123456", "delta"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "A", "#123456", "delta"));   // tail too short
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "TST", "blue", "delta"));    // bad colour
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "TST", "#123456", "nope")); // unknown emblem
    }

    [Fact]
    public async Task Standing_FreshCompany_IsStartup()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();

        var standing = await Svc(db).GetStandingAsync(company.Id, pilot.Id);

        Assert.Equal(0, standing.Tier);
        Assert.Equal("Startup", standing.TierName);
        Assert.Equal(40, standing.NextTierScore);
    }

    [Fact]
    public async Task Standing_RisesWithReputationAndScale()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        pilot.ReputationMilli = 80_000; // 80 points
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHEH", IsActive = true }); // 3 bases → 24 points
        await db.SaveChangesAsync();

        var standing = await Svc(db).GetStandingAsync(company.Id, pilot.Id);

        Assert.Equal(104, standing.Score);          // 80 + 24
        Assert.Equal("National", standing.TierName); // >= 100
        Assert.Contains(standing.Contributions, c => c.Label == "Pilot reputation" && c.Points == 80);
        Assert.DoesNotContain(standing.Contributions, c => c.Points == 0); // zero levers are hidden
    }

    [Fact]
    public async Task Standing_IncludesAirlineReputationContribution()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        company.OperatingReputationMilli = 40_000; // Phase 11a — an earned airline name → 40 points
        await db.SaveChangesAsync();

        var standing = await Svc(db).GetStandingAsync(company.Id, pilot.Id);

        Assert.Contains(standing.Contributions, c => c.Label == "Airline reputation" && c.Points == 40);
    }

    [Fact]
    public async Task Standing_FreshCompany_HidesTheAirlineReputationLever()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db); // OperatingReputationMilli defaults to 0
        await db.SaveChangesAsync();

        var standing = await Svc(db).GetStandingAsync(company.Id, pilot.Id);

        Assert.DoesNotContain(standing.Contributions, c => c.Label == "Airline reputation"); // a 0 lever is hidden, as before
    }

    private static async Task<(Guid CompanyId, Guid PilotId)> SeedSavedAsync(TestDb tdb)
    {
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();
        return (company.Id, pilot.Id);
    }
}
