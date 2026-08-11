using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class LedgerServiceTests
{
    private static async Task<Company> SeedAccountAsync(CallsignDbContext db)
    {
        var c = new Company { Id = Guid.NewGuid(), Name = "Test Co" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task Post_WritesEntry_AndKeepsCachedCash_EqualToLedgerSum()
    {
        using var tdb = new TestDb();
        Guid accountId;

        using (var db = tdb.NewContext())
        {
            var account = await SeedAccountAsync(db);
            accountId = account.Id;
            var ledger = new LedgerService(db, new FakeClock());
            await ledger.PostAsync(accountId, LedgerCategory.JobPayout, 1250.50m, "Cargo run");
            await ledger.PostAsync(accountId, LedgerCategory.Fuel, -80.25m, "Refuel");
        }

        using (var db = tdb.NewContext())
        {
            var account = await db.Companies.FindAsync(accountId);
            var entries = await db.LedgerEntries.Where(e => e.AccountId == accountId).ToListAsync();

            Assert.Equal(2, entries.Count);
            Assert.Equal(117025, account!.CashCents);                        // 125050 - 8025
            Assert.Equal(entries.Sum(e => e.AmountCents), account.CashCents); // invariant
            Assert.Equal(1170.25m, account.Cash);
            Assert.All(entries, e => Assert.NotEqual(Guid.Empty, e.EntryUid));
        }
    }

    [Fact]
    public async Task PostBatch_IsAtomic_OneTimestamp_OneBalanceUpdate()
    {
        using var tdb = new TestDb();
        Guid accountId;

        using (var db = tdb.NewContext())
        {
            var account = await SeedAccountAsync(db);
            accountId = account.Id;
            var ledger = new LedgerService(db, new FakeClock());
            var lines = new[]
            {
                new LedgerPosting(LedgerCategory.JobPayout, 1000m, "base reward", LedgerRefType.Job, "job-1"),
                new LedgerPosting(LedgerCategory.JobBonus, 150m, "landing bonus", LedgerRefType.Job, "job-1"),
                new LedgerPosting(LedgerCategory.AirportFee, -75m, "arrival fee", LedgerRefType.Job, "job-1"),
            };
            var entries = await ledger.PostBatchAsync(accountId, lines);
            Assert.Equal(3, entries.Count);
        }

        using (var db = tdb.NewContext())
        {
            var account = await db.Companies.FindAsync(accountId);
            var entries = await db.LedgerEntries.Where(e => e.AccountId == accountId).ToListAsync();

            Assert.Equal(3, entries.Count);
            Assert.Equal(107500, account!.CashCents);                        // 100000 + 15000 - 7500
            Assert.Equal(entries.Sum(e => e.AmountCents), account.CashCents);
            Assert.Single(entries.Select(e => e.At).Distinct());             // one time = one transaction
        }
    }

    [Fact]
    public async Task GetBalanceCents_EqualsCachedCash()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var account = await SeedAccountAsync(db);
        var ledger = new LedgerService(db, new FakeClock());

        await ledger.PostAsync(account.Id, LedgerCategory.StartingBalance, 50000m, "start");
        await ledger.PostAsync(account.Id, LedgerCategory.AircraftPurchase, -32000m, "buy");

        var balance = await ledger.GetBalanceCentsAsync(account.Id);
        Assert.Equal(1_800_000, balance);        // (50000 - 32000) * 100
        Assert.Equal(account.CashCents, balance);
    }

    [Fact]
    public async Task Post_UnknownAccount_Throws()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var ledger = new LedgerService(db, new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.PostAsync(Guid.NewGuid(), LedgerCategory.Adjustment, 1m, "x"));
    }

    [Fact]
    public async Task Post_DuplicateDedupeKey_SameAccount_IsRejected()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var account = await SeedAccountAsync(db);
        var ledger = new LedgerService(db, new FakeClock());

        await ledger.PostAsync(account.Id, LedgerCategory.StaffWage, -500m, "wage", dedupeKey: "wage:2026-08-11");
        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => ledger.PostAsync(account.Id, LedgerCategory.StaffWage, -500m, "wage dup", dedupeKey: "wage:2026-08-11"));
    }

    [Fact]
    public async Task Reconcile_HealsCacheDrift_FromLedger()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        var account = await SeedAccountAsync(db);
        var ledger = new LedgerService(db, new FakeClock());
        await ledger.PostAsync(account.Id, LedgerCategory.StartingBalance, 1000m, "start");

        // Force a drift, as if a reused context had left the cache desynced (ApplyCashDelta is internal).
        account.ApplyCashDelta(50_000);
        await db.SaveChangesAsync();
        Assert.NotEqual(await ledger.GetBalanceCentsAsync(account.Id), account.CashCents);

        var reconciled = await ledger.ReconcileAsync(account.Id);
        Assert.Equal(100_000, reconciled);          // ledger truth: 1000 * 100
        Assert.Equal(100_000, account.CashCents);   // cache healed
    }

    [Theory]
    [InlineData(0.005, 1)]     // rounds away from zero
    [InlineData(-0.005, -1)]
    [InlineData(12.34, 1234)]
    [InlineData(0, 0)]
    public void ToCents_Rounds(decimal amount, long expected)
        => Assert.Equal(expected, LedgerService.ToCents(amount));
}
