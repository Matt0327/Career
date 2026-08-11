using Callsign.Core.Domain;

namespace Callsign.Core.Progression;

/// <summary>A rank tier: the cumulative XP to reach it, plus self-documenting display text (shipped to the UI).</summary>
public sealed record RankTier(PilotRank Rank, int MinXp, string DisplayName, string Description);

/// <summary>
/// The rank ladder (Phase 3a). Reference content — self-documenting so the UI never hard-codes a
/// threshold. Promotion is by cumulative XP; ranks act as a job filter (3b), never a reward multiplier.
/// <see cref="All"/> is ordered ascending by <see cref="RankTier.MinXp"/>.
/// </summary>
public static class RankTiers
{
    public static readonly IReadOnlyList<RankTier> All =
    [
        new(PilotRank.Trainee,        0,      "Trainee",        "Just getting started — short regional runs."),
        new(PilotRank.Copilot,        500,    "Copilot",        "Proven on the basics; longer legs open up."),
        new(PilotRank.Captain,        2_000,  "Captain",        "Trusted with demanding jobs and bigger aircraft."),
        new(PilotRank.SeniorCaptain,  6_000,  "Senior Captain", "A seasoned hand for the toughest contracts."),
        new(PilotRank.Chief,          15_000, "Chief Pilot",    "The top of the ladder — nothing is off-limits."),
    ];

    /// <summary>The highest rank whose XP threshold this pilot has reached.</summary>
    public static PilotRank ForXp(int xp)
    {
        var rank = PilotRank.Trainee;
        foreach (var t in All)
            if (xp >= t.MinXp)
                rank = t.Rank;
        return rank;
    }

    /// <summary>The tier above <paramref name="rank"/>, or null at the top of the ladder.</summary>
    public static RankTier? Next(PilotRank rank)
    {
        for (int i = 0; i < All.Count - 1; i++)
            if (All[i].Rank == rank)
                return All[i + 1];
        return null;
    }

    public static RankTier Def(PilotRank rank) => All.First(t => t.Rank == rank);
}
