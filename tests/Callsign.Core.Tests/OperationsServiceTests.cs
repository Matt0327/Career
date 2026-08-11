using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class OperationsServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    [Fact]
    public async Task Reconcile_BooksTrips_Fees_AndWages_ViaLedger_AndIsIdempotent()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, staffId, aircraftId;

        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM" };
            db.AircraftInstances.Add(inst);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            companyId = company.Id;
            aircraftId = inst.Id;
        }

        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            var candidate = ops.GenerateCandidates(companyId.GetHashCode())[0];
            staffId = (await ops.HireAsync(companyId, candidate.Seed)).Id;
            await ops.CreateStandingOrderAsync(companyId, staffId, aircraftId, "EHRD");
        }

        clock.UtcNow = clock.UtcNow.AddDays(2); // two days pass while the app is "closed"

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.True(digest.Trips > 0);
        Assert.Equal(digest.GrossIncomeCents - digest.FeesCents - digest.WagesCents, digest.NetCents);
        Assert.True(digest.WagesCents > 0);

        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // the invariant survives autonomous income
            var cats = (await db.LedgerEntries.ToListAsync()).Select(e => e.Category).ToHashSet();
            Assert.Contains(LedgerCategory.StaffWage, cats);
            Assert.Contains(LedgerCategory.AirportFee, cats);
            Assert.True((await db.AircraftInstances.FindAsync(aircraftId))!.AirframeHours > 0); // the airframe flew
        }

        using (var db = tdb.NewContext())
        {
            var again = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);
            Assert.Equal(0, again.Trips); // no time passed since → nothing double-booked
        }
    }
}
