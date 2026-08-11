using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class LedgerServiceTests
{
    private static async Task<Pilot> SeedPilotAsync(CallsignDbContext db)
    {
        var p = new Pilot { Id = Guid.NewGuid(), Name = "Test", HomeIcao = "EHAM", CurrentIcao = "EHAM" };
        db.Pilots.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Post_WritesEntry_AndKeepsCachedCash_EqualToLedgerSum()
    {
        using var tdb = new TestDb();
        Guid pilotId;

        using (var db = tdb.NewContext())
        {
            var pilot = await SeedPilotAsync(db);
            pilotId = pilot.Id;
            var ledger = new LedgerService(db, new FakeClock());
            await ledger.PostAsync(pilotId, LedgerCategory.JobPayout, 1250.50m, "Cargo run");
            await ledger.PostAsync(pilotId, LedgerCategory.Fuel, -80.25m, "Fuel");
        }

        using (var db = tdb.NewContext())
        {
            var pilot = await db.Pilots.FindAsync(pilotId);
            var entries = await db.LedgerEntries.Where(e => e.PilotId == pilotId).ToListAsync();

            Assert.Equal(2, entries.Count);
            Assert.Equal(117025, pilot!.CashCents);              // 125050 - 8025
            Assert.Equal(entries.Sum(e => e.AmountCents), pilot.CashCents); // invariant
            Assert.Equal(1170.25m, pilot.Cash);
        }
    }

    [Fact]
    public async Task GetBalanceCents_EqualsCachedCash()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var pilot = await SeedPilotAsync(db);
        var ledger = new LedgerService(db, new FakeClock());

        await ledger.PostAsync(pilot.Id, LedgerCategory.StartingBalance, 50000m, "start");
        await ledger.PostAsync(pilot.Id, LedgerCategory.AircraftPurchase, -32000m, "buy");

        var balance = await ledger.GetBalanceCentsAsync(pilot.Id);
        Assert.Equal(1_800_000, balance);        // (50000 - 32000) * 100
        Assert.Equal(pilot.CashCents, balance);
    }

    [Fact]
    public async Task Post_UnknownPilot_Throws()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var ledger = new LedgerService(db, new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.PostAsync(Guid.NewGuid(), LedgerCategory.Adjustment, 1m, "x"));
    }

    [Theory]
    [InlineData(0.005, 1)]     // rounds away from zero
    [InlineData(-0.005, -1)]
    [InlineData(12.34, 1234)]
    [InlineData(0, 0)]
    public void ToCents_Rounds(decimal amount, long expected)
        => Assert.Equal(expected, LedgerService.ToCents(amount));
}
