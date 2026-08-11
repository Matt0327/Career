using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class MissionJobSourceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static JobGenerationRequest Req(int seed, int count = 8) => new(
        "EHAM",
        [new JobCandidate("EHRD", 24), new JobCandidate("EHEH", 63), new JobCandidate("EGLL", 200), new JobCandidate("KFAR", 900)],
        PilotRank.Trainee, count, seed);

    [Fact]
    public void Cargo_Reward_And_Xp_Follow_Multipliers()
    {
        var def = MissionCatalog.Def(MissionType.Cargo);
        var jobs = new MissionJobSource(def, Cfg).Generate(Req(7));
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j =>
        {
            Assert.Equal(MissionType.Cargo, j.Type);
            Assert.Equal(0, j.Pax);
            Assert.InRange(j.WeightLbs, Cfg.MinCargoWeightLbs, Cfg.MaxCargoWeightLbs);
            Assert.Equal((long)Math.Round(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs) * def.RewardMult), j.RewardCents);
            Assert.Equal((int)Math.Round(Cfg.JobXp(j.DistanceNm) * def.XpMult), j.Xp);
            Assert.DoesNotContain("KFAR", j.DestIcao); // out of range excluded
        });
    }

    [Fact]
    public void Hazardous_Pays_More_Than_Cargo_And_Gates_By_Rank()
    {
        var haz = MissionCatalog.Def(MissionType.Hazardous);
        var jobs = new MissionJobSource(haz, Cfg).Generate(Req(9));
        Assert.All(jobs, j =>
        {
            Assert.Equal(MissionType.Hazardous, j.Type);
            Assert.True(j.RequiredRank >= PilotRank.Captain);                                   // mission minimum enforced
            Assert.True(j.RewardCents > Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs));        // premium over plain cargo
        });
    }

    [Fact]
    public void PassengerLike_Missions_CarryPax_AndPriceOffPaxFormula()
    {
        var vip = MissionCatalog.Def(MissionType.Vip);
        var jobs = new MissionJobSource(vip, Cfg).Generate(Req(3));
        Assert.All(jobs, j =>
        {
            Assert.Equal(MissionType.Vip, j.Type);
            Assert.InRange(j.Pax, Cfg.MinPax, Cfg.MaxPax);
            Assert.Equal(j.Pax * Cfg.PaxWeightLbs, j.WeightLbs);
            Assert.Equal((long)Math.Round(Cfg.PaxRewardCents(j.DistanceNm, j.Pax) * vip.RewardMult), j.RewardCents);
        });
    }

    [Fact]
    public void RequiredRank_IsHarderOf_DistanceAndMissionMinimum()
    {
        // Express requires at least Copilot; even a short (Trainee-by-distance) leg is gated up.
        var jobs = new MissionJobSource(MissionCatalog.Def(MissionType.Express), Cfg).Generate(Req(5));
        Assert.All(jobs, j => Assert.True(j.RequiredRank >= PilotRank.Copilot));
    }

    [Fact]
    public void IsDeterministic_PerSeed()
    {
        var def = MissionCatalog.Def(MissionType.Express);
        var a = new MissionJobSource(def, Cfg).Generate(Req(42));
        var b = new MissionJobSource(def, Cfg).Generate(Req(42));
        Assert.Equal(
            a.Select(j => (j.DestIcao, j.RewardCents, j.RequiredRank)),
            b.Select(j => (j.DestIcao, j.RewardCents, j.RequiredRank)));
    }
}
