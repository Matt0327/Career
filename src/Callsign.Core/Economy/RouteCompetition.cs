namespace Callsign.Core.Economy;

/// <summary>
/// Competition on a scheduled route (Phase 14b). Every city-pair already has incumbents; this generates a stable,
/// invented (clean-room) set of RIVAL carriers for a route and computes YOUR share of the market from how you
/// stack up on reputation and fare. Your share flexes the cabin fill: out-reputation and under-price the rivals to
/// win share, and the seats follow. Pure + deterministic (rivals are seeded off the route, share is a function of
/// stored values), and pump-safe — the share only ever becomes a BOUNDED multiplier on an already-bounded,
/// income-only load factor, and never touches the two-sided commodity market.
/// </summary>
public static class RouteCompetition
{
    // Invented carrier name parts — clean-room, deliberately generic aviation words (no real airline).
    private static readonly string[] Marks =
        { "Cirrus", "Meridian", "Northstar", "Vanguard", "Zephyr", "Solstice", "Aurora", "Cardinal",
          "Summit", "Halcyon", "Beacon", "Orion", "Tamarind", "Estuary", "Cobalt", "Marlin" };
    private static readonly string[] Suffixes =
        { "Air", "Wings", "Regional", "Airlines", "Connect", "Express", "Skyways", "Link" };

    public sealed record Rival(string Name, int ReputationMilli, int FareMultiplierMilli);
    /// <summary><see cref="YourShareMilli"/> is your ACTUAL share after rival pressure; <see cref="RawShareMilli"/>
    /// is your underlying dominance (rep + fare) BEFORE pressure — what the reconcile uses to decide whether the
    /// rivals mobilise further.</summary>
    public sealed record Standing(double YourShareMilli, double LoadMultiplier, IReadOnlyList<Rival> Rivals, double RawShareMilli);

    /// <summary>The invented rivals contesting a route — a stable 1..MaxRivals set seeded off the route id, each
    /// with its own reputation and fare posture. Deterministic, so the competitive landscape is fixed per route.</summary>
    public static IReadOnlyList<Rival> Rivals(EconomyConfig cfg, Guid routeId)
    {
        uint h = Hash(routeId);
        int n = cfg.CompetitionMinRivals + (int)(h % (uint)(cfg.CompetitionMaxRivals - cfg.CompetitionMinRivals + 1));
        var list = new List<Rival>(n);
        for (int i = 0; i < n; i++)
        {
            uint s = Hash(routeId, i);
            string name = $"{Marks[(int)(s % (uint)Marks.Length)]} {Suffixes[(int)((s / 16) % (uint)Suffixes.Length)]}";
            // Incumbents span a believable band: reputation 40–90, fare 90%–120% of market.
            int rep = 40_000 + (int)((s / 128) % 50_000u);
            int fare = 900 + (int)((s / 4096) % 300u);
            list.Add(new Rival(name, rep, fare));
        }
        return list;
    }

    /// <summary>How attractive a carrier is to a passenger: a reputation pull times a price pull (cheaper wins).
    /// Weights are gentle so neither lever alone dominates.</summary>
    public static double Attractiveness(EconomyConfig cfg, int reputationMilli, int fareMultiplierMilli)
    {
        double repPull = cfg.CompetitionRepFloor + (1 - cfg.CompetitionRepFloor) * Math.Clamp(reputationMilli / 100_000.0, 0, 1);
        double pricePull = Math.Pow(1000.0 / Math.Max(1, fareMultiplierMilli), cfg.CompetitionPriceExponent);
        return Math.Max(0.01, repPull * pricePull);
    }

    /// <summary>Your market share and the resulting BOUNDED load multiplier, given your reputation and fare against
    /// the route's rivals. Share above your "fair share" (an even split) lifts the cabin; below it, rivals bleed
    /// you — capped at ±<see cref="EconomyConfig.CompetitionLoadSwing"/> so it flavours demand, never dominates it.</summary>
    public static Standing Evaluate(EconomyConfig cfg, Guid routeId, int yourRepMilli, int yourFareMilli, int rivalPressureMilli = 0)
    {
        var rivals = Rivals(cfg, routeId);
        double you = Attractiveness(cfg, yourRepMilli, yourFareMilli);
        double baseRivalSum = rivals.Sum(r => Attractiveness(cfg, r.ReputationMilli, r.FareMultiplierMilli));
        double neutral = 1.0 / (1 + rivals.Count);          // an even split among all carriers
        double rawShare = you / (you + baseRivalSum);       // your underlying dominance, before pressure

        // Phase 16e — organised rivals (accumulated pressure) fight harder for the cabin: their pull is boosted,
        // so your share and the resulting load fall. Bounded — pressure only ever feeds the already-clamped swing.
        double pressuredRivalSum = baseRivalSum * (1 + Math.Clamp(rivalPressureMilli, 0, 100_000) / 100_000.0 * cfg.CompetitionPressureRivalBoost);
        double share = you / (you + pressuredRivalSum);
        double rel = share >= neutral ? (share - neutral) / (1 - neutral) : (share - neutral) / neutral; // [-1, 1]
        double mult = Math.Clamp(1 + cfg.CompetitionLoadSwing * rel, 1 - cfg.CompetitionLoadSwing, 1 + cfg.CompetitionLoadSwing);
        return new Standing(share * 1000.0, mult, rivals, rawShare * 1000.0);
    }

    /// <summary>The rival-pressure TARGET (thousandths, 0..100000) your current dominance provokes: sitting above a
    /// fair share mobilises the rivals in proportion to how far above you are; at or below fair share, they stand
    /// down (target 0). A Network Planner's competitive defence scales the target down. Pure.</summary>
    public static int PressureTarget(EconomyConfig cfg, Guid routeId, double rawShareMilli, double networkPlannerDefense)
    {
        int rivalCount = Rivals(cfg, routeId).Count;
        double neutral = 1000.0 / (1 + rivalCount);
        double over = rawShareMilli <= neutral ? 0 : (rawShareMilli - neutral) / (1000.0 - neutral); // [0,1]
        double target = over * 100_000.0 * (1 - Math.Clamp(networkPlannerDefense, 0, 0.95));
        return (int)Math.Round(Math.Clamp(target, 0, 100_000));
    }

    // FNV-1a over the guid (+ an optional salt) — deterministic across runs, unlike Guid.GetHashCode.
    private static uint Hash(Guid id, int salt = 0)
    {
        uint h = 2166136261;
        foreach (var b in id.ToByteArray()) { h ^= b; h *= 16777619; }
        unchecked { h ^= (uint)salt; h *= 16777619; }
        return h;
    }
}
