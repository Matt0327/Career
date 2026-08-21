using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// The one local job generator (Phase 3e), driven by a <see cref="MissionDef"/>. Deterministic given a
/// seed: it draws near-biased destinations, sizes the load (seats for passenger-carrying missions, freight
/// weight otherwise), and prices the reward/XP off the base cargo/passenger formulas scaled by the mission
/// multipliers. The rank gate is the harder of the leg's distance rank and the mission's minimum.
/// </summary>
public sealed class MissionJobSource : IJobSource
{
    private readonly MissionDef _def;
    private readonly EconomyConfig _cfg;

    public MissionJobSource(MissionDef def, EconomyConfig cfg)
    {
        _def = def;
        _cfg = cfg;
    }

    public IReadOnlyList<GeneratedJob> Generate(JobGenerationRequest request)
    {
        // Phase 12 — the board only offers work the pilot can actually take. If this whole mission type sits above
        // the pilot's rank, it produces nothing (the composite backfills the board from the missions they CAN fly);
        // and destinations far enough to demand a higher rank than they hold are dropped, so a leg is never posted
        // stamped above the pilot. As they rank up, longer legs and premium mission types appear on their own.
        if (_def.MinRank > request.PilotRank)
            return Array.Empty<GeneratedJob>();
        var eligible = request.Candidates
            .Where(c => c.DistanceNm >= _cfg.MinJobDistanceNm && c.DistanceNm <= _cfg.MaxJobDistanceNm)
            .Where(c => _cfg.RankForDistance(c.DistanceNm) <= request.PilotRank)
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
            var label = _def.Labels[rng.Next(_def.Labels.Length)];

            int weightLbs, pax;
            long baseReward;
            if (_def.CarriesPassengers)
            {
                pax = rng.Next(_cfg.MinPax, _cfg.MaxPax + 1);
                weightLbs = pax * _cfg.PaxWeightLbs;
                baseReward = _cfg.PaxRewardCents(dest.DistanceNm, pax);
            }
            else
            {
                weightLbs = rng.Next(_cfg.MinCargoWeightLbs, _cfg.MaxCargoWeightLbs + 1);
                pax = 0;
                baseReward = _cfg.CargoRewardCents(dest.DistanceNm, weightLbs);
            }

            long reward = (long)Math.Round(baseReward * _def.RewardMult);
            int xp = (int)Math.Round(_cfg.JobXp(dest.DistanceNm) * _def.XpMult);
            var rank = (PilotRank)Math.Max((int)_cfg.RankForDistance(dest.DistanceNm), (int)_def.MinRank);

            jobs.Add(new GeneratedJob(_def.Type, request.OriginIcao, dest.DestIcao, label,
                weightLbs, pax, dest.DistanceNm, reward, xp, rank));
        }
        return jobs;
    }
}
