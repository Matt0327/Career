using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class NewGameServiceTests
{
    [Fact]
    public async Task StartNewCareer_SeedsPilot_WithStartingBalanceAsLedgerEntry()
    {
        using var tdb = new TestDb();
        Guid pilotId;

        using (var db = tdb.NewContext())
        {
            var ledger = new LedgerService(db, new FakeClock());
            var svc = new NewGameService(db, ledger);
            var pilot = await svc.StartNewCareerAsync("Amelia", "EHAM", 25000m);
            pilotId = pilot.Id;

            Assert.Equal(PilotRank.Trainee, pilot.Rank);
            Assert.Equal("EHAM", pilot.HomeIcao);
            Assert.Equal("EHAM", pilot.CurrentIcao);
        }

        using (var db = tdb.NewContext())
        {
            var pilot = await db.Pilots.FindAsync(pilotId);
            var entries = await db.LedgerEntries.Where(e => e.PilotId == pilotId).ToListAsync();

            var start = Assert.Single(entries);
            Assert.Equal(LedgerCategory.StartingBalance, start.Category);
            Assert.Equal(2_500_000, start.AmountCents);
            Assert.Equal(25000m, pilot!.Cash);
            Assert.Equal(start.AmountCents, pilot.CashCents); // truth == ledger
        }
    }
}
