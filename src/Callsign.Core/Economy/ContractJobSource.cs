using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// Phase 11d — the contract on-ramp. Generates "flying-the-majors'-overflow" cargo work for a set of invented
/// carriers (data-only, clean-room): steady, lower-margin freight that a one-plane outfit takes on early, the
/// distinct flavour of the bottom rung of the airline ladder. It is a plain <see cref="IJobSource"/> producing
/// ordinary <see cref="MissionType.Cargo"/> jobs — so it settles through the EXISTING settlement path with no
/// new table, wallet, or money seam — but priced BELOW the owner margin it substitutes
/// (<see cref="EconomyConfig.ContractCarrierPayFactor"/> &lt; 1.0). It is the safe, lower-yield option, never a
/// higher-yield idle pump. Each job is tagged with its <see cref="GeneratedJob.ContractCarrier"/> so the board
/// brands the carrier as the client and withholds the hub-reputation lift (the carrier's rate is fixed; your
/// name lifts your OWN freelance work, not theirs — 11c/11d).
/// </summary>
public sealed class ContractJobSource : IJobSource
{
    // Invented carriers (clean-room — not real operators). Data only; the "employer" is never an account root.
    private static readonly string[] Carriers =
    [
        "Aerolux Cargo", "Meridian Air Freight", "SkyLink Logistics",
        "Continental Overland Air", "Nordwind Air Cargo", "Pacifica Freight",
    ];

    private static readonly string[] Freight =
    [
        "general freight", "machine parts", "palletised goods", "consumer electronics",
        "auto components", "packaged foods", "building supplies", "sorted mail",
    ];

    private readonly EconomyConfig _cfg;

    public ContractJobSource(EconomyConfig cfg) => _cfg = cfg;

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
                available.AddRange(eligible);
            int idx = JobRng.NearBiasedIndex(rng, available.Count, _cfg.JobDistanceBiasExponent);
            var dest = available[idx];
            available.RemoveAt(idx);
            int weight = rng.Next(_cfg.MinCargoWeightLbs, _cfg.MaxCargoWeightLbs + 1);
            var carrier = Carriers[rng.Next(Carriers.Length)];
            var freight = Freight[rng.Next(Freight.Length)];

            // Priced BELOW the freelance owner rate — the whole point of the on-ramp (the safe path yields less).
            long reward = (long)Math.Round(_cfg.CargoRewardCents(dest.DistanceNm, weight) * _cfg.ContractCarrierPayFactor);

            jobs.Add(new GeneratedJob(
                MissionType.Cargo,
                request.OriginIcao,
                dest.DestIcao,
                $"Overflow — {freight}",
                weight,
                Pax: 0,
                dest.DistanceNm,
                reward,
                _cfg.JobXp(dest.DistanceNm),
                _cfg.RankForDistance(dest.DistanceNm),
                ContractCarrier: carrier));
        }
        return jobs;
    }
}
