using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// One mission type's economics (Phase 3e). Reward and XP are the base cargo/passenger figures scaled by
/// <see cref="RewardMult"/> / <see cref="XpMult"/>; <see cref="ReputationMilliReward"/> is what a delivery
/// earns (Phase 3f). Gates: <see cref="MinRank"/> (folded into the job's distance rank) and
/// <see cref="MinReputationMilli"/> (0 until the reputation-gated types arrive in 3f). Self-documenting.
/// </summary>
public sealed record MissionDef(
    MissionType Type,
    string DisplayName,
    double RewardMult,
    double XpMult,
    int ReputationMilliReward,
    bool CarriesPassengers,
    PilotRank MinRank,
    int MinReputationMilli,
    double BoardShare,
    string[] Labels);

/// <summary>
/// The mission types the board generates and their economics — original, freely retunable. Cargo and
/// passenger are the staples; the rest are premium variants gated by rank (and, from 3f, reputation).
/// Reputation-gated missions (Emergency/SAR, Illicit) join in Phase 3f.
/// </summary>
public static class MissionCatalog
{
    private static readonly string[] Freight =
        ["General freight", "Machine parts", "Foodstuffs", "Medical supplies", "Building materials", "Electronics", "Auto parts", "Mail"];
    private static readonly string[] Groups =
        ["Business travellers", "Holiday charter", "Film crew", "Sports team", "Corporate group", "Wedding party", "Survey team", "Tour group"];

    public static readonly IReadOnlyList<MissionDef> All =
    [
        new(MissionType.Cargo,     "Cargo",     1.00, 1.00, 30, false, PilotRank.Trainee,  0, 34,
            Freight),
        new(MissionType.Passenger, "Passenger", 1.00, 1.05, 50, true,  PilotRank.Trainee,  0, 22,
            Groups),
        new(MissionType.Express,   "Express",   1.25, 1.10, 40, false, PilotRank.Copilot,  0, 14,
            ["Urgent documents", "Express parcels", "Priority freight", "Time-critical spares", "Overnight courier"]),
        new(MissionType.Tourist,   "Tourist",   1.15, 1.20, 60, true,  PilotRank.Trainee,  0, 12,
            ["Sightseers", "Photo tour", "Scenic charter", "Holiday hop", "Coastal tour"]),
        new(MissionType.Sensitive, "Sensitive", 1.55, 1.25, 70, false, PilotRank.Captain,  0,  7,
            ["Fine art", "Lab samples", "Aerospace components", "Medical isotopes", "Prototype hardware"]),
        new(MissionType.Hazardous, "Hazardous", 1.80, 1.35, 80, false, PilotRank.Captain,  0,  5,
            ["Industrial chemicals", "Compressed gas", "Explosives", "Fuel drums", "Reactor components"]),
        new(MissionType.Vip,       "VIP",       1.60, 1.10, 90, true,  PilotRank.Captain,  0,  6,
            ["Executive", "Diplomatic party", "Corporate board", "Visiting dignitary", "Private client"]),
    ];

    /// <summary>The mission types currently mixed onto the board.</summary>
    public static IReadOnlyList<MissionDef> Generated => All;

    public static MissionDef Def(MissionType type) => All.First(d => d.Type == type);
}
