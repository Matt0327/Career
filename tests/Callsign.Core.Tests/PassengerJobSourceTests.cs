using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class PassengerJobSourceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static JobGenerationRequest Req(int seed) => new(
        "EHAM",
        [
            new JobCandidate("EHRD", 24),
            new JobCandidate("EHEH", 63),
            new JobCandidate("EGLL", 200),
            new JobCandidate("KFAR", 900), // out of range — must be excluded
        ],
        PilotRank.Trainee, Count: 6, Seed: seed);

    [Fact]
    public void Generate_IsDeterministic_ProducesPassengerJobs_WithinBounds()
    {
        var src = new PassengerJobSource(Cfg);
        var a = src.Generate(Req(7));
        var b = src.Generate(Req(7));

        Assert.Equal(6, a.Count);
        Assert.Equal(
            a.Select(j => (j.DestIcao, j.Pax, j.RewardCents)),
            b.Select(j => (j.DestIcao, j.Pax, j.RewardCents)));
        Assert.DoesNotContain(a, j => j.DestIcao == "KFAR");
        Assert.All(a, j =>
        {
            Assert.Equal(MissionType.Passenger, j.Type);
            Assert.InRange(j.Pax, Cfg.MinPax, Cfg.MaxPax);
            Assert.Equal(j.Pax * Cfg.PaxWeightLbs, j.WeightLbs);          // weight = bodies + bags
            Assert.Equal(Cfg.PaxRewardCents(j.DistanceNm, j.Pax), j.RewardCents);
            Assert.InRange(j.DistanceNm, Cfg.MinJobDistanceNm, Cfg.MaxJobDistanceNm);
            Assert.True(j.Xp > 0);
        });
    }

    [Fact]
    public void Reward_ScalesWith_PaxAndDistance()
    {
        Assert.True(Cfg.PaxRewardCents(100, 4) > Cfg.PaxRewardCents(100, 2));  // more people, more money
        Assert.True(Cfg.PaxRewardCents(200, 3) > Cfg.PaxRewardCents(100, 3));  // farther, more money
    }

    [Fact]
    public void Reward_MatchesFormula()
        => Assert.Equal(
            Cfg.PaxBaseFeeCents + 4 * (Cfg.PaxPerPaxCents + (long)Math.Round(150 * (double)Cfg.PaxPerPaxNmCents)),
            Cfg.PaxRewardCents(150, 4));
}
