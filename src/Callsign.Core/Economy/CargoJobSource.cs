using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// Local generator of freelance Cargo jobs. Deterministic given a seed so refreshes are reproducible
/// and testable. Reward and XP come from <see cref="EconomyConfig"/> — never inline literals.
/// </summary>
public sealed class CargoJobSource : IJobSource
{
    private static readonly string[] Commodities =
    [
        "General freight", "Machine parts", "Foodstuffs", "Medical supplies",
        "Building materials", "Electronics", "Auto parts", "Mail",
    ];

    private readonly EconomyConfig _cfg;

    public CargoJobSource(EconomyConfig cfg) => _cfg = cfg;

    public IReadOnlyList<GeneratedJob> Generate(JobGenerationRequest request)
    {
        var eligible = request.Candidates
            .Where(c => c.DistanceNm >= _cfg.MinJobDistanceNm && c.DistanceNm <= _cfg.MaxJobDistanceNm)
            .OrderBy(c => c.DistanceNm)
            .ToList();
        if (eligible.Count == 0)
            return Array.Empty<GeneratedJob>();

        var rng = new Random(request.Seed);
        var jobs = new List<GeneratedJob>(request.Count);
        for (int i = 0; i < request.Count; i++)
        {
            var dest = eligible[NearBiasedIndex(rng, eligible.Count)];
            int weight = rng.Next(_cfg.MinCargoWeightLbs, _cfg.MaxCargoWeightLbs + 1);
            var commodity = Commodities[rng.Next(Commodities.Length)];

            jobs.Add(new GeneratedJob(
                MissionType.Cargo,
                request.OriginIcao,
                dest.DestIcao,
                commodity,
                weight,
                Pax: 0,
                dest.DistanceNm,
                _cfg.CargoRewardCents(dest.DistanceNm, weight),
                _cfg.JobXp(dest.DistanceNm),
                PilotRank.Trainee));
        }
        return jobs;
    }

    /// <summary>
    /// Draw an index into a nearest-first list, biased toward the front so short regional hops appear
    /// reliably. A uniform draw over a wide radius is area-dominated (far airports vastly outnumber
    /// near ones), so without this the board is almost all long hauls. The tail still reaches the
    /// occasional long haul; <see cref="EconomyConfig.JobDistanceBiasExponent"/> controls the lean.
    /// </summary>
    private int NearBiasedIndex(Random rng, int count)
        => Math.Min((int)(Math.Pow(rng.NextDouble(), _cfg.JobDistanceBiasExponent) * count), count - 1);
}
