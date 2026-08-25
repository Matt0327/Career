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
        var available = new List<JobCandidate>(eligible); // draw without replacement so destinations don't repeat
        for (int i = 0; i < request.Count; i++)
        {
            if (available.Count == 0)
                available.AddRange(eligible); // more jobs than distinct destinations — allow repeats
            int idx = JobRng.NearBiasedIndex(rng, available.Count, _cfg.JobDistanceBiasExponent);
            var dest = available[idx];
            available.RemoveAt(idx);
            int weight = _cfg.BiasedLoad(rng.NextDouble(), _cfg.MinCargoWeightLbs, _cfg.MaxCargoWeightLbs); // small loads common (light aircraft always have work)
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
                _cfg.RankForDistance(dest.DistanceNm)));
        }
        return jobs;
    }
}
