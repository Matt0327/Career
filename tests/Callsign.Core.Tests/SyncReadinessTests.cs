using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Guards the server-ready invariants that ADR-0002 (shared-world economy authority) relies on. If any
/// of these fail, future work has silently foreclosed the shared-world option — the ADR's whole point.
/// </summary>
public class SyncReadinessTests
{
    [Fact]
    public void SyncableAggregates_ImplementISyncable()
    {
        var aggregates = new[]
        {
            typeof(Pilot), typeof(AircraftInstance), typeof(Base), typeof(InventoryLot),
            typeof(PilotQualification), typeof(Loan), typeof(InsurancePolicy), typeof(Route),
            typeof(AchievementAward), typeof(CampaignProgress),
        };
        Assert.All(aggregates, t => Assert.True(typeof(ISyncable).IsAssignableFrom(t),
            $"{t.Name} must implement ISyncable to stay server-ready (ADR-0002)."));
    }

    [Fact]
    public async Task EveryLedgerPosting_GetsAGlobalMergeKey()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var entry = await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100m, "start");
        Assert.NotEqual(Guid.Empty, entry.EntryUid); // the globally-unique idempotency / merge key
    }

    [Fact]
    public void Ledger_KeepsItsUniqueMergeKeyIndexes()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var uniques = db.Model.FindEntityType(typeof(LedgerEntry))!.GetIndexes().Where(i => i.IsUnique).ToList();
        Assert.Contains(uniques, i => i.Properties.Any(p => p.Name == nameof(LedgerEntry.EntryUid)));
        Assert.Contains(uniques, i => i.Properties.Any(p => p.Name == nameof(LedgerEntry.DedupeKey)));
    }
}
