using System.Globalization;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Challenges;

public sealed record ChallengeView(
    string Key, string Title, string Detail, ChallengeCadence Cadence,
    long Target, long Progress, long RewardCents, bool Done, bool Claimed, DateTimeOffset ResetsAt);

public sealed record ClaimResult(bool Ok, string? Error, long PaidCents, ChallengeView? Challenge);

/// <summary>
/// The rotating daily/weekly challenges (Phase 12 — retention). A challenge measures the GROWTH of a shared
/// progress metric over its period: the first time it's seen this period a baseline is captured, and progress is
/// <c>current − baseline</c>. Completing one lets the player CLAIM a one-off cash reward through the ledger (paid
/// once, dedupe-guarded). Reads advance nothing on their own — unlike a campaign, a challenge is claimed by hand,
/// so the reward is a deliberate collect, not an automatic drip. The board rotates by period key; a new day/week
/// starts fresh rows with fresh baselines. Money flows only on an explicit claim of a genuinely-met challenge.
/// </summary>
public sealed class ChallengesService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly ProgressMetricsService _metrics;
    private readonly LedgerService _ledger;

    public ChallengesService(CallsignDbContext db, IClock clock, ProgressMetricsService metrics, LedgerService ledger)
    {
        _db = db;
        _clock = clock;
        _metrics = metrics;
        _ledger = ledger;
    }

    public async Task<IReadOnlyList<ChallengeView>> GetActiveAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);
        var now = _clock.UtcNow;
        var active = ActiveDefs(now);

        var periodKeys = active.Select(a => a.PeriodKey).Distinct().ToList();
        var rows = (await _db.ChallengeProgress
                .Where(c => c.CompanyId == companyId && periodKeys.Contains(c.PeriodKey) && !c.IsDeleted)
                .ToListAsync(ct))
            .ToDictionary(c => (c.PeriodKey, c.ChallengeKey));

        var pendingSave = false;
        var views = new List<ChallengeView>(active.Count);
        foreach (var a in active)
        {
            var row = Ensure(companyId, a, m, rows, now, ref pendingSave);
            views.Add(ToView(a, row, m));
        }

        if (pendingSave) await _db.SaveChangesAsync(ct);
        return views;
    }

    public async Task<ClaimResult> ClaimAsync(Guid companyId, Guid pilotId, string challengeKey, CancellationToken ct = default)
    {
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);
        var now = _clock.UtcNow;
        var a = ActiveDefs(now).FirstOrDefault(x => x.Def.Key == challengeKey);
        if (a is null) return new ClaimResult(false, "That challenge isn't live right now.", 0, null);

        var row = await _db.ChallengeProgress.FirstOrDefaultAsync(
            c => c.CompanyId == companyId && c.PeriodKey == a.PeriodKey && c.ChallengeKey == challengeKey && !c.IsDeleted, ct);
        if (row is null)
        {
            // Never seen this period — capturing the baseline now means zero progress, so it can't be complete yet.
            var seed = new Dictionary<(string, string), ChallengeProgress>();
            var pending = false;
            row = Ensure(companyId, a, m, seed, now, ref pending);
            if (pending) await _db.SaveChangesAsync(ct);
        }

        if (row.ClaimedAt is not null)
            return new ClaimResult(false, "Already claimed.", 0, ToView(a, row, m));

        long progress = Progress(a.Def, row, m);
        if (progress < a.Def.Target)
            return new ClaimResult(false, "Not finished yet.", 0, ToView(a, row, m));

        row.ClaimedAt = now;
        row.UpdatedAt = now;
        // PostAsync commits the whole unit of work (the reward row + cash + the ClaimedAt flip) in one
        // transaction; the dedupe key makes the payout idempotent even if two claims race.
        await _ledger.PostAsync(companyId, LedgerCategory.ChallengeReward, a.Def.RewardCents / 100m,
            $"Challenge reward — {a.Def.Title}", LedgerRefType.Challenge, $"{a.PeriodKey}:{a.Def.Key}",
            dedupeKey: $"challenge:{a.PeriodKey}:{a.Def.Key}", ct: ct);

        return new ClaimResult(true, null, a.Def.RewardCents, ToView(a, row, m));
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────

    private sealed record ActiveChallenge(ChallengeDef Def, string PeriodKey, DateTimeOffset ResetsAt);

    private static IReadOnlyList<ActiveChallenge> ActiveDefs(DateTimeOffset now)
    {
        var (dailyKey, dailyReset) = DailyPeriod(now);
        var (weeklyKey, weeklyReset) = WeeklyPeriod(now);
        var list = new List<ActiveChallenge>();
        foreach (var d in ChallengeCatalog.ForPeriod(ChallengeCadence.Daily, dailyKey))
            list.Add(new ActiveChallenge(d, dailyKey, dailyReset));
        foreach (var d in ChallengeCatalog.ForPeriod(ChallengeCadence.Weekly, weeklyKey))
            list.Add(new ActiveChallenge(d, weeklyKey, weeklyReset));
        return list;
    }

    private ChallengeProgress Ensure(
        Guid companyId, ActiveChallenge a, ProgressMetrics m,
        Dictionary<(string, string), ChallengeProgress> rows, DateTimeOffset now, ref bool pendingSave)
    {
        if (rows.TryGetValue((a.PeriodKey, a.Def.Key), out var row)) return row;
        row = new ChallengeProgress
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            PeriodKey = a.PeriodKey,
            ChallengeKey = a.Def.Key,
            Baseline = a.Def.Metric(m),   // progress is measured from here, so it only counts what you do this period
            UpdatedAt = now,
        };
        _db.ChallengeProgress.Add(row);
        rows[(a.PeriodKey, a.Def.Key)] = row;
        pendingSave = true;
        return row;
    }

    private static long Progress(ChallengeDef def, ChallengeProgress row, ProgressMetrics m)
        => Math.Max(0, def.Metric(m) - row.Baseline);

    private static ChallengeView ToView(ActiveChallenge a, ChallengeProgress row, ProgressMetrics m)
    {
        long progress = Progress(a.Def, row, m);
        return new ChallengeView(
            a.Def.Key, a.Def.Title, a.Def.Detail, a.Def.Cadence, a.Def.Target,
            Math.Min(progress, a.Def.Target), a.Def.RewardCents,
            Done: progress >= a.Def.Target, Claimed: row.ClaimedAt is not null, ResetsAt: a.ResetsAt);
    }

    private static (string Key, DateTimeOffset ResetsAt) DailyPeriod(DateTimeOffset now)
    {
        var d = now.UtcDateTime.Date;
        var key = $"D:{d:yyyy-MM-dd}";
        var resets = new DateTimeOffset(d.AddDays(1), TimeSpan.Zero);
        return (key, resets);
    }

    private static (string Key, DateTimeOffset ResetsAt) WeeklyPeriod(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        int isoWeek = ISOWeek.GetWeekOfYear(utc);
        int isoYear = ISOWeek.GetYear(utc);
        var key = $"W:{isoYear}-{isoWeek:D2}";
        // ISO weeks start Monday; the period resets at the next Monday 00:00 UTC.
        int daysSinceMonday = ((int)utc.DayOfWeek + 6) % 7;
        var nextMonday = utc.Date.AddDays(7 - daysSinceMonday);
        var resets = new DateTimeOffset(nextMonday, TimeSpan.Zero);
        return (key, resets);
    }
}
