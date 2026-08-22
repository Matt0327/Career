using Callsign.Core.Progression;

namespace Callsign.Core.Challenges;

/// <summary>How often a challenge resets.</summary>
public enum ChallengeCadence { Daily = 1, Weekly = 2 }

/// <summary>
/// One rotating challenge (Phase 12): a short, time-boxed goal measured as the GROWTH of a shared progress
/// metric over the period — "fly 3 legs today," "grease 8 landings this week" — that pays a small cash reward
/// on completion. Unlike a <c>CampaignDef</c> (a lifetime arc), a challenge reads the DELTA against a baseline
/// captured when the period began, so it's about what you do <em>now</em>, not what you've ever done.
/// </summary>
public sealed record ChallengeDef(
    string Key, string Title, string Detail, ChallengeCadence Cadence, long Target,
    Func<ProgressMetrics, long> Metric, long RewardCents);

/// <summary>
/// The challenge pool + the deterministic per-period rotation. Each period a fixed number of daily and weekly
/// challenges are drawn from the pool by a stable hash of the period key, so the board is the same for a given
/// day/week (learnable, shareable) yet shifts as time turns. Pure and stateless — the roll is a function of the
/// period key alone.
/// </summary>
public static class ChallengeCatalog
{
    public const int DailyCount = 2;
    public const int WeeklyCount = 2;

    public static readonly IReadOnlyList<ChallengeDef> All = new[]
    {
        // ── Daily ──────────────────────────────────────────────────────────────────
        new ChallengeDef("d-legs-3", "Wheels up", "Fly 3 legs today.", ChallengeCadence.Daily, 3, m => m.Flights, 8_000_00),
        new ChallengeDef("d-legs-6", "A full day", "Fly 6 legs today.", ChallengeCadence.Daily, 6, m => m.Flights, 16_000_00),
        new ChallengeDef("d-smooth-2", "Smooth operator", "Grease 2 landings (within 60 fpm) today.", ChallengeCadence.Daily, 2, m => m.SmoothLandings, 10_000_00),
        new ChallengeDef("d-earn-25k", "Turn a profit", "Grow your net worth by $25,000 today.", ChallengeCadence.Daily, 25_000_00, m => m.NetWorthCents, 9_000_00),

        // ── Weekly ─────────────────────────────────────────────────────────────────
        new ChallengeDef("w-legs-20", "Line pilot", "Fly 20 legs this week.", ChallengeCadence.Weekly, 20, m => m.Flights, 55_000_00),
        new ChallengeDef("w-smooth-10", "Consistency", "Grease 10 landings this week.", ChallengeCadence.Weekly, 10, m => m.SmoothLandings, 60_000_00),
        new ChallengeDef("w-earn-200k", "Growth quarter", "Grow your net worth by $200,000 this week.", ChallengeCadence.Weekly, 200_000_00, m => m.NetWorthCents, 70_000_00),
    };

    /// <summary>The challenges live this period for a given cadence — a deterministic draw from the pool by the
    /// period key, so everyone sees the same board for that day/week and it rotates cleanly as the period turns.</summary>
    public static IReadOnlyList<ChallengeDef> ForPeriod(ChallengeCadence cadence, string periodKey)
    {
        int take = cadence == ChallengeCadence.Daily ? DailyCount : WeeklyCount;
        return All.Where(c => c.Cadence == cadence)
            .OrderBy(c => Hash($"{periodKey}|{c.Key}"))
            .Take(take)
            .ToList();
    }

    // A stable FNV-1a hash so the rotation is identical across processes/runs (unlike string.GetHashCode).
    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h;
    }
}
