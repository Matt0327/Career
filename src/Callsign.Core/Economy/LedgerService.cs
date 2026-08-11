using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>One money movement to post. Positive amount = credit, negative = debit.</summary>
public readonly record struct LedgerPosting(
    LedgerCategory Category,
    decimal Amount,
    string Description,
    LedgerRefType? RelatedEntityType = null,
    string? RelatedEntityId = null,
    Guid? AircraftInstanceId = null,
    Guid? StaffId = null,
    Guid? BaseId = null,
    string? DedupeKey = null);

/// <summary>
/// The one sanctioned way money moves. Every posting writes ledger row(s) AND updates the
/// account's cached balance in a SINGLE transaction, so <see cref="Company.CashCents"/>
/// always equals the sum of the account's ledger entries (brief §6: "Do not compute
/// balances any other way"). Keeping all movement here also keeps us server-ready — a
/// future shared-world server could become authoritative over the ledger without changing
/// callers.
/// </summary>
public sealed class LedgerService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;

    public LedgerService(CallsignDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Post a single money movement atomically.</summary>
    public async Task<LedgerEntry> PostAsync(
        Guid accountId, LedgerCategory category, decimal amount, string description,
        LedgerRefType? relatedType = null, string? relatedEntityId = null,
        Guid? aircraftInstanceId = null, Guid? staffId = null, Guid? baseId = null,
        string? dedupeKey = null, CancellationToken ct = default)
    {
        var posting = new LedgerPosting(category, amount, description, relatedType, relatedEntityId,
            aircraftInstanceId, staffId, baseId, dedupeKey);
        var entries = await PostBatchAsync(accountId, new[] { posting }, ct);
        return entries[0];
    }

    /// <summary>
    /// Post N money movements plus the shared cached-balance update in ONE transaction. Use
    /// for settlement, where a payout is several itemised lines that must all land or none —
    /// a crash mid-settlement must never overstate cash or desync the breakdown.
    /// </summary>
    public async Task<IReadOnlyList<LedgerEntry>> PostBatchAsync(
        Guid accountId, IReadOnlyCollection<LedgerPosting> postings, CancellationToken ct = default)
    {
        var entries = await StageBatchAsync(accountId, postings, ct);
        if (entries.Count > 0)
            await _db.SaveChangesAsync(ct); // single transaction: N rows + the balance update
        return entries;
    }

    /// <summary>
    /// Stage the ledger entries + the account balance update on the context WITHOUT saving, so a
    /// caller (e.g. settlement) can commit them together with its own side-effects — the Flight row,
    /// XP, status — in a single transaction. Returns the (unsaved) entries.
    /// </summary>
    internal async Task<IReadOnlyList<LedgerEntry>> StageBatchAsync(
        Guid accountId, IReadOnlyCollection<LedgerPosting> postings, CancellationToken ct = default)
    {
        if (postings.Count == 0)
            return Array.Empty<LedgerEntry>();

        var account = await _db.Companies.FirstOrDefaultAsync(c => c.Id == accountId, ct)
                      ?? throw new InvalidOperationException($"Account {accountId} not found.");

        var now = _clock.UtcNow;
        var entries = new List<LedgerEntry>(postings.Count);
        long delta = 0;

        foreach (var p in postings)
        {
            var cents = ToCents(p.Amount);
            delta += cents;
            entries.Add(new LedgerEntry
            {
                EntryUid = Guid.NewGuid(),
                AccountId = accountId,
                At = now,
                Category = p.Category,
                AmountCents = cents,
                Description = p.Description,
                RelatedEntityType = p.RelatedEntityType,
                RelatedEntityId = p.RelatedEntityId,
                AircraftInstanceId = p.AircraftInstanceId,
                StaffId = p.StaffId,
                BaseId = p.BaseId,
                DedupeKey = p.DedupeKey,
            });
        }

        _db.LedgerEntries.AddRange(entries);
        account.ApplyCashDelta(delta);
        return entries;
    }

    /// <summary>Authoritative balance: the sum of the account's ledger entries, in cents.</summary>
    public Task<long> GetBalanceCentsAsync(Guid accountId, CancellationToken ct = default)
        => _db.LedgerEntries.Where(e => e.AccountId == accountId).SumAsync(e => e.AmountCents, ct);

    /// <summary>Convert a decimal currency amount to integer cents.</summary>
    internal static long ToCents(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
