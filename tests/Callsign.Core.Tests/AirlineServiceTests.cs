using Callsign.Core.Airline;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Microsoft.EntityFrameworkCore;
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

    // Phase 13 — mark a seeded company as already incorporated, so identity-setting (now gated) is allowed.
    private static async Task MarkIncorporatedAsync(TestDb tdb, Guid companyId)
    {
        using var db = tdb.NewContext();
        var c = await db.Companies.FindAsync(companyId);
        c!.AirlineIncorporatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();
    }

    private static (Company Company, Pilot Pilot) Seed(CallsignDbContext db, string name = "SkyBridge Air")
    {
        var company = new Company { Id = Guid.NewGuid(), Name = name };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Companies.Add(company);
        db.Pilots.Add(pilot);
        return (company, pilot);
    }

    // "The Flotation" (Phase 13): GetMarketAsync marks a value snapshot at most once per interval for an
    // incorporated airline, building the share-price ticker; an operator (not incorporated) records nothing.
    [Fact]
    public async Task Market_RecordsOnePerInterval_ForAnIncorporatedAirline_AndPricesTheHistory()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var cfg = EconomyConfig.Default;
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var (c, _) = Seed(db);
            companyId = c.Id;
            await db.SaveChangesAsync();
        }

        AirlineService Svc(CallsignDbContext db) => new(db, new ProgressMetricsService(db, new FinanceService(db, clock, cfg)),
            clock: clock, cfg: cfg);

        // Not incorporated → no marks, but the share price still reads.
        using (var db = tdb.NewContext())
        {
            var r = await Svc(db).GetMarketAsync(companyId, 5_000_000_000, incorporated: false);
            Assert.Equal(5_000, r.SharePriceCents);
            Assert.Empty(r.History);
            Assert.Equal(0, await db.AirlineValueSnapshots.CountAsync());
        }

        // Incorporated: first read marks the flotation baseline.
        using (var db = tdb.NewContext())
            await Svc(db).GetMarketAsync(companyId, 4_000_000_000, incorporated: true);
        // A second read moments later is inside the interval → no new mark.
        clock.UtcNow = clock.UtcNow.AddHours(1);
        using (var db = tdb.NewContext())
            await Svc(db).GetMarketAsync(companyId, 4_200_000_000, incorporated: true);
        using (var db = tdb.NewContext())
            Assert.Equal(1, await db.AirlineValueSnapshots.CountAsync());

        // A day later, past the interval → a second mark; growth is measured off the baseline.
        clock.UtcNow = clock.UtcNow.AddHours(24);
        using (var db = tdb.NewContext())
        {
            var r = await Svc(db).GetMarketAsync(companyId, 5_000_000_000, incorporated: true);
            Assert.Equal(2, await db.AirlineValueSnapshots.CountAsync());
            Assert.Equal(0.25, r.GrowthSinceFlotationPct, 3);   // $40M floated → $50M now = +25%
            Assert.Equal(4_000, r.FlotationSharePriceCents);
        }
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
        await MarkIncorporatedAsync(tdb, companyId); // Phase 13 — naming is unlocked only after incorporation

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
        await MarkIncorporatedAsync(tdb, companyId);
        using var db = tdb.NewContext();
        var svc = Svc(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "", "TST", "#123456", "delta"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "A", "#123456", "delta"));   // tail too short
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "TST", "blue", "delta"));    // bad colour
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetIdentityAsync(companyId, "OK Air", "TST", "#123456", "nope")); // unknown emblem
    }

    [Fact]
    public async Task Incorporation_FreshOperator_NotEligible_AndRefused()
    {
        using var tdb = new TestDb();
        var (companyId, pilotId) = await SeedSavedAsync(tdb);
        using var db = tdb.NewContext();
        var svc = Svc(db);
        var status = await svc.GetIncorporationStatusAsync(companyId, pilotId);
        Assert.False(status.Incorporated);
        Assert.False(status.RegionalReached); // a fresh operator is Contract Operator (stage 0)
        Assert.False(status.Eligible);
        // Can't found an airline yet — the Regional gate refuses it.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.IncorporateAsync(companyId, pilotId, "Test Air", "TST", "#4f46e5", "delta"));
    }

    [Fact]
    public async Task Incorporation_LegacyNamedCompany_CountsAsIncorporated()
    {
        using var tdb = new TestDb();
        var (companyId, pilotId) = await SeedSavedAsync(tdb);
        using (var db = tdb.NewContext()) { var c = await db.Companies.FindAsync(companyId); c!.AirlineName = "Old Air"; await db.SaveChangesAsync(); }
        using var db2 = tdb.NewContext();
        Assert.True((await Svc(db2).GetIncorporationStatusAsync(companyId, pilotId)).Incorporated); // grandfathered
    }

    [Fact]
    public async Task Standing_FreshCompany_IsContractOperator()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var (company, pilot) = Seed(db);
        await db.SaveChangesAsync();

        var standing = await Svc(db).GetStandingAsync(company.Id, pilot.Id);

        Assert.Equal(0, standing.Stage);
        Assert.Equal("Contract Operator", standing.StageName);
        Assert.Equal(5, standing.Stages.Count);
        Assert.True(standing.Stages[0].Reached);
        Assert.False(standing.Stages[1].Reached);
        Assert.Equal("Charter Operator", standing.NextMove!.StageName); // the climb points at the next rung, never a wall
    }

    [Fact]
    public async Task Standing_Score_RisesWithReputationAndScale()
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

        Assert.Equal(104, standing.Score);          // 80 + 24 — the informational operating score is unchanged (11a math intact)
        Assert.Contains(standing.Contributions, c => c.Label == "Pilot reputation" && c.Points == 80);
        Assert.DoesNotContain(standing.Contributions, c => c.Points == 0); // zero levers are hidden
        // But raw score no longer sets the STAGE: a Trainee with no fleet/rep/net-worth is still Contract Operator (11b AND-gate).
        Assert.Equal(0, standing.Stage);
        Assert.Equal("Contract Operator", standing.StageName);
    }

    [Fact]
    public async Task Standing_ReachesRegional_WithFullOperation()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, pilotId;
        using (var db = tdb.NewContext())
        {
            var (company, pilot) = Seed(db);
            pilot.Rank = PilotRank.Captain;               // Regional needs Captain
            company.OperatingReputationMilli = 30_000;    // ...and 25.0 operating reputation
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var tails = new List<AircraftInstance>();
            for (int i = 0; i < 4; i++)                   // ...4 owned tails
            {
                var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = $"CS-{i}", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, LocationIcao = "EHAM" };
                tails.Add(inst);
                db.AircraftInstances.Add(inst);
            }
            var crew = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Crew", SkillMilli = 60_000, WagePerDayCents = 10_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(crew);
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true }); // ...2 bases
            for (int i = 0; i < 3; i++)                   // ...3 routes (FKs point at real tails + crew)
                db.Routes.Add(new Route { Id = Guid.NewGuid(), CompanyId = company.Id, Name = $"Line {i}", OriginIcao = "EHAM", DestIcao = "EHRD", AircraftInstanceId = tails[i].Id, StaffId = crew.Id });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 700_000m, "seed"); // ...net worth well over $600k
            companyId = company.Id; pilotId = pilot.Id;
        }

        using var db2 = tdb.NewContext();
        var standing = await Svc(db2).GetStandingAsync(companyId, pilotId);

        Assert.Equal(2, standing.Stage);
        Assert.Equal("Regional", standing.StageName);
        Assert.True(standing.Stages[2].Reached);
        Assert.False(standing.Stages[3].Reached); // Captain < Senior Captain holds it at Regional (National not yet reached)
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
