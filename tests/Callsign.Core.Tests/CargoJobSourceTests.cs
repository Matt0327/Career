using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class CargoJobSourceTests
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
    public void Generate_IsDeterministic_AndExcludesOutOfRange()
    {
        var src = new CargoJobSource(Cfg);
        var a = src.Generate(Req(42));
        var b = src.Generate(Req(42));

        Assert.Equal(6, a.Count);
        Assert.Equal(
            a.Select(j => (j.DestIcao, j.WeightLbs, j.RewardCents)),
            b.Select(j => (j.DestIcao, j.WeightLbs, j.RewardCents)));
        Assert.DoesNotContain(a, j => j.DestIcao == "KFAR");
        Assert.All(a, j =>
        {
            Assert.Equal(MissionType.Cargo, j.Type);
            Assert.InRange(j.DistanceNm, Cfg.MinJobDistanceNm, Cfg.MaxJobDistanceNm);
            Assert.True(j.WeightLbs is >= 200 and <= 3000);
            Assert.Equal(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs), j.RewardCents);
            Assert.True(j.Xp > 0);
        });
    }

    [Theory]
    [InlineData(100, 1000)]
    [InlineData(250, 500)]
    public void Reward_MatchesFormula(double dist, int weight)
        => Assert.Equal(Cfg.CargoBaseFeeCents + (long)Math.Round(dist * Cfg.CargoPerNmCents) + weight * Cfg.CargoPerLbCents, Cfg.CargoRewardCents(dist, weight));

    [Fact]
    public void Reward_ScalesUp_WithDistanceAndWeight()
    {
        Assert.True(Cfg.CargoRewardCents(200, 1000) > Cfg.CargoRewardCents(100, 1000));
        Assert.True(Cfg.CargoRewardCents(100, 2000) > Cfg.CargoRewardCents(100, 1000));
    }

    [Fact]
    public void Xp_IsPositive_AndScalesWithDistance()
    {
        Assert.Equal(5 + 20, Cfg.JobXp(200)); // 5 + round(200 * 0.1)
        Assert.True(Cfg.JobXp(300) > Cfg.JobXp(100));
    }
}
