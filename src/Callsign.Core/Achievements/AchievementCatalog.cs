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
        new AchievementDef("iron-pilot", "Iron Pilot", "Settle 500 flights.", "Flying", 500, m => m.Flights),
        new AchievementDef("thousand-club", "Thousand Club", "Settle 1,000 flights.", "Flying", 1_000, m => m.Flights),
        new AchievementDef("butter", "Butter", "Grease a landing at 60 fpm or softer.", "Flying", 1, m => m.SmoothLandings),
        new AchievementDef("smooth-operator", "Smooth Operator", "Ten butter-smooth landings.", "Flying", 10, m => m.SmoothLandings),
        new AchievementDef("silk-hands", "Silk Hands", "Fifty butter-smooth landings.", "Flying", 50, m => m.SmoothLandings),
        new AchievementDef("perfectionist", "Perfectionist", "Score 95+ on a flight.", "Flying", 95, m => m.BestScore),
        new AchievementDef("flawless", "Flawless", "Score a perfect 100 on a flight.", "Flying", 100, m => m.BestScore),

        // ── Distance: the miles add up ──
        new AchievementDef("cross-country", "Cross-Country", "Fly a single leg of 500 nm.", "Distance", 500, m => m.LongestLegNm),
        new AchievementDef("long-hauler", "Long-Hauler", "Fly a single leg of 2,000 nm.", "Distance", 2_000, m => m.LongestLegNm),
        new AchievementDef("well-travelled", "Well Travelled", "Log 10,000 nm total.", "Distance", 10_000, m => m.TotalDistanceNm),
        new AchievementDef("globetrotter", "Globetrotter", "Log 100,000 nm total.", "Distance", 100_000, m => m.TotalDistanceNm),
        new AchievementDef("round-the-world", "Round the World", "Log 21,600 nm — once around the planet.", "Distance", 21_600, m => m.TotalDistanceNm),

        // ── Career: rank, ratings, reputation ──
        new AchievementDef("copilot", "Second Seat", "Reach the rank of Copilot.", "Career", (int)PilotRank.Copilot, m => m.RankIndex),
        new AchievementDef("command", "In Command", "Reach the rank of Captain.", "Career", (int)PilotRank.Captain, m => m.RankIndex),
        new AchievementDef("senior", "Senior Captain", "Reach the rank of Senior Captain.", "Career", (int)PilotRank.SeniorCaptain, m => m.RankIndex),
        new AchievementDef("chief", "Chief", "Reach the top rank.", "Career", (int)PilotRank.Chief, m => m.RankIndex),
        new AchievementDef("well-rated", "Well Rated", "Hold three licence classes.", "Career", 3, m => m.Qualifications),
        new AchievementDef("fully-rated", "Fully Rated", "Hold five licence classes.", "Career", 5, m => m.Qualifications),
        new AchievementDef("trusted", "Trusted", "Build your reputation to 60.", "Career", 60_000, m => m.ReputationMilli),
        new AchievementDef("household-name", "Household Name", "Build your reputation to 90.", "Career", 90_000, m => m.ReputationMilli),

        // ── Business: scale ──
        new AchievementDef("second-plane", "Second Plane", "Own two aircraft.", "Business", 2, m => m.Aircraft),
        new AchievementDef("fleet", "Fleet", "Own three aircraft.", "Business", 3, m => m.Aircraft),
        new AchievementDef("air-armada", "Air Armada", "Own ten aircraft.", "Business", 10, m => m.Aircraft),
        new AchievementDef("network", "Network", "Operate three bases.", "Business", 3, m => m.Bases),
        new AchievementDef("empire", "Empire", "Operate ten bases.", "Business", 10, m => m.Bases),
        new AchievementDef("on-the-line", "On the Line", "Open a scheduled route.", "Business", 1, m => m.Routes),
        new AchievementDef("timetable", "Timetable", "Run five scheduled routes.", "Business", 5, m => m.Routes),
        new AchievementDef("regular", "A Regular", "Serve five different clients.", "Business", 5, m => m.Clients),
        new AchievementDef("preferred-carrier", "Preferred Carrier", "Serve twenty different clients.", "Business", 20, m => m.Clients),
        new AchievementDef("well-regarded", "Well Regarded", "Reach 60 operating reputation.", "Business", 60_000, m => m.OperatingReputationMilli),

        // ── Finance: the balance sheet ──
        new AchievementDef("first-payday", "First Payday", "Earn $100,000 lifetime.", "Finance", 10_000_000, m => m.LifetimeEarningsCents),
        new AchievementDef("well-earned", "Well Earned", "Earn $5,000,000 lifetime.", "Finance", 500_000_000, m => m.LifetimeEarningsCents),
        new AchievementDef("millionaire", "Millionaire", "Reach a net worth of $1,000,000.", "Finance", 100_000_000, m => m.NetWorthCents),
        new AchievementDef("mogul", "Mogul", "Reach a net worth of $25,000,000.", "Finance", 2_500_000_000, m => m.NetWorthCents),
        new AchievementDef("debt-free", "Debt-Free", "Pay off a loan in full.", "Finance", 1, m => m.LoansPaidOff),
        new AchievementDef("good-credit", "Good Credit", "Pay off three loans in full.", "Finance", 3, m => m.LoansPaidOff),
        new AchievementDef("covered", "Covered", "Insure an airframe.", "Finance", 1, m => m.Policies),
    };
}
