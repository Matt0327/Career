using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class NewGameServiceTests
{
    private static AircraftType Type(string designator, string name)
        => new() { Id = Guid.NewGuid(), Key = designator, CanonicalName = name,
                   IcaoTypeDesignator = designator, Category = AircraftCategory.LightSingle };

    private static async Task SeedStarterTypesAsync(CallsignDbContext db)
    {
        db.AircraftTypes.AddRange(
            Type("C152", "Cessna 152"),
            Type("DR40", "Robin DR400"),
            Type("VL3", "JMB VL3"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task StartNewCareer_GrantsTheChosenStarterPlane_AndRecordsIdentity()
    {
        using var tdb = new TestDb();
        using (var db = tdb.NewContext()) await SeedStarterTypesAsync(db);

        Guid companyId, pilotId;
        using (var db = tdb.NewContext())
        {
            var svc = new NewGameService(db, new LedgerService(db, new FakeClock()), new FakeClock());
            var (company, pilot) = await svc.StartNewCareerAsync(
                "Maverick", "KLAX", 10000m, starterTypeCode: "VL3", edition: "Deluxe", avatarKey: "fox");
            companyId = company.Id;
            pilotId = pilot.Id;
        }

        using (var db = tdb.NewContext())
        {
            var vl3 = await db.AircraftTypes.FirstAsync(t => t.IcaoTypeDesignator == "VL3");
            var granted = await db.AircraftInstances.SingleAsync(a => a.CompanyId == companyId);
            Assert.Equal(vl3.Id, granted.TypeId);              // the plane they picked, not the alphabetical default
            Assert.Equal("KLAX", granted.LocationIcao);

            Assert.Equal("Deluxe", (await db.Companies.FindAsync(companyId))!.MsfsEdition);
            Assert.Equal("fox", (await db.Pilots.FindAsync(pilotId))!.AvatarKey);
        }
    }

    [Fact]
    public async Task StartNewCareer_UnknownOrMissingStarter_FallsBackToALightSingle()
    {
        using var tdb = new TestDb();
        using (var db = tdb.NewContext()) await SeedStarterTypesAsync(db);

        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var svc = new NewGameService(db, new LedgerService(db, new FakeClock()), new FakeClock());
            // No pick at all — an old caller (or a skipped step) must still get a flyable airframe.
            var (company, _) = await svc.StartNewCareerAsync("Nobody", "EHAM", 10000m);
            companyId = company.Id;
        }

        using (var db = tdb.NewContext())
        {
            var granted = await db.AircraftInstances.SingleAsync(a => a.CompanyId == companyId);
            var type = await db.AircraftTypes.FindAsync(granted.TypeId);
            Assert.Equal(AircraftCategory.LightSingle, type!.Category);
        }
    }

    [Fact]
    public void Catalog_IncludesTheStarterTrio_AllLightSingles()
    {
        var byCode = DefaultFleetCatalog.Aircraft2024.ToDictionary(a => a.IcaoTypeDesignator);
        foreach (var code in new[] { "C152", "DR40", "VL3" })
        {
            Assert.True(byCode.ContainsKey(code), $"starter {code} missing from the catalog");
            Assert.Equal(AircraftCategory.LightSingle, byCode[code].Category);
        }
    }

    [Fact]
    public async Task StartNewCareer_SeedsCompanyAndPilot_WithStartingBalanceOnAccount()
    {
        using var tdb = new TestDb();
        Guid companyId, pilotId;

        using (var db = tdb.NewContext())
        {
            var ledger = new LedgerService(db, new FakeClock());
            var svc = new NewGameService(db, ledger, new FakeClock());
            var (company, pilot) = await svc.StartNewCareerAsync("Amelia", "EHAM", 25000m);
            companyId = company.Id;
            pilotId = pilot.Id;

            Assert.Equal(PilotRank.Trainee, pilot.Rank);
            Assert.Equal(company.Id, pilot.CompanyId);
            Assert.Equal("EHAM", pilot.HomeIcao);
        }

        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var pilot = await db.Pilots.FindAsync(pilotId);
            var entries = await db.LedgerEntries.Where(e => e.AccountId == companyId).ToListAsync();

            var start = Assert.Single(entries);
            Assert.Equal(LedgerCategory.StartingBalance, start.Category);
            Assert.Equal(2_500_000, start.AmountCents);
            Assert.Equal(25000m, company!.Cash);
            Assert.Equal(start.AmountCents, company.CashCents); // truth == ledger
            Assert.Equal(companyId, pilot!.CompanyId);
        }
    }
}
