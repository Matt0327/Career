using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// Local generator of freelance passenger charters — a group of travellers to a nearby field. Mirrors
/// the cargo generator's deterministic, near-biased draw, but the load is measured in SEATS: the
/// reward scales with how many people and how far they fly. Weight is bodies + bags, so an airframe's
/// useful load still bites (you can't cram six into a two-seater). Deterministic given a seed.
/// </summary>
public sealed class PassengerJobSource : IJobSource
{
    private static readonly string[] Groups =
    [
        "Business travellers", "Holiday charter", "Film crew", "Sports team",
        "Corporate group", "Wedding party", "Survey team", "Tour group",
    ];

    private readonly EconomyConfig _cfg;

    public PassengerJobSource(EconomyConfig cfg) => _cfg = cfg;

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
            int pax = _cfg.BiasedLoad(rng.NextDouble(), _cfg.MinPax, _cfg.MaxPax); // smaller groups common — a 2-seater always has charters
            var group = Groups[rng.Next(Groups.Length)];

            jobs.Add(new GeneratedJob(
                MissionType.Passenger,
                request.OriginIcao,
                dest.DestIcao,
                group,
                WeightLbs: pax * _cfg.PaxWeightLbs,
                Pax: pax,
                dest.DistanceNm,
                _cfg.PaxRewardCents(dest.DistanceNm, pax),
                _cfg.JobXp(dest.DistanceNm),
                _cfg.RankForDistance(dest.DistanceNm)));
        }
        return jobs;
    }
}
