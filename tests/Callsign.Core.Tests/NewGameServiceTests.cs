using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class NewGameServiceTests
{
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
