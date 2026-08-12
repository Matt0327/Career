using Callsign.Core.Domain;
using Callsign.Core.Progression;

namespace Callsign.Core.Achievements;

/// <summary>
/// One earnable milestone. <see cref="Metric"/> pulls the tracked number out of the shared
/// <see cref="ProgressMetrics"/> snapshot and the badge is earned once it reaches <see cref="Target"/> —
/// so the same definition also yields a progress bar for the locked state. Definitions live in code and
/// are self-documenting; only the *earning* of one is persisted (an <see cref="AchievementAward"/>).
/// </summary>
public sealed record AchievementDef(
    string Key, string Name, string Description, string Category, long Target, Func<ProgressMetrics, long> Metric)
{
    public long ProgressOf(ProgressMetrics m) => Math.Max(0, Metric(m));
    public bool IsEarnedBy(ProgressMetrics m) => Metric(m) >= Target;
}

/// <summary>An achievement as shown to the player: the definition plus this company's state against it.</summary>
public sealed record AchievementView(
    string Key, string Name, string Description, string Category, long Target, long Progress,
    bool Earned, DateTimeOffset? EarnedAt);

/// <summary>
/// The full achievement roster (Phase 5a) — data-driven and self-documenting, in the spirit of the other
/// catalogs (MissionCatalog, LoanCatalog). Every badge reads off metrics the game already tracks, so the
/// set can grow without new plumbing. Grouped by <c>Category</c> for display.
/// </summary>
public static class AchievementCatalog
{
    public static readonly IReadOnlyList<AchievementDef> All = new[]
    {
        // ── Flying: the core loop ──
        new AchievementDef("first-flight", "First Flight", "Settle your first job.", "Flying", 1, m => m.Flights),
        new AchievementDef("frequent-flyer", "Frequent Flyer", "Settle 25 flights.", "Flying", 25, m => m.Flights),
        new AchievementDef("centurion", "Centurion", "Settle 100 flights.", "Flying", 100, m => m.Flights),
        new AchievementDef("butter", "Butter", "Grease a landing at 60 fpm or softer.", "Flying", 1, m => m.SmoothLandings),
        new AchievementDef("smooth-operator", "Smooth Operator", "Ten butter-smooth landings.", "Flying", 10, m => m.SmoothLandings),

        // ── Career: rank, ratings, reputation ──
        new AchievementDef("copilot", "Second Seat", "Reach the rank of Copilot.", "Career", (int)PilotRank.Copilot, m => m.RankIndex),
        new AchievementDef("command", "In Command", "Reach the rank of Captain.", "Career", (int)PilotRank.Captain, m => m.RankIndex),
        new AchievementDef("chief", "Chief", "Reach the top rank.", "Career", (int)PilotRank.Chief, m => m.RankIndex),
        new AchievementDef("well-rated", "Well Rated", "Hold three licence classes.", "Career", 3, m => m.Qualifications),
        new AchievementDef("trusted", "Trusted", "Build your reputation to 60.", "Career", 60_000, m => m.ReputationMilli),

        // ── Business: scale ──
        new AchievementDef("fleet", "Fleet", "Own three aircraft.", "Business", 3, m => m.Aircraft),
        new AchievementDef("network", "Network", "Operate three bases.", "Business", 3, m => m.Bases),
        new AchievementDef("on-the-line", "On the Line", "Open a scheduled route.", "Business", 1, m => m.Routes),

        // ── Finance: the balance sheet ──
        new AchievementDef("millionaire", "Millionaire", "Reach a net worth of $1,000,000.", "Finance", 100_000_000, m => m.NetWorthCents),
        new AchievementDef("debt-free", "Debt-Free", "Pay off a loan in full.", "Finance", 1, m => m.LoansPaidOff),
        new AchievementDef("covered", "Covered", "Insure an airframe.", "Finance", 1, m => m.Policies),
    };
}
