using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class CompositeJobSourceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static JobGenerationRequest Req(int count, int seed) => new(
        "EHAM",
        [
            new JobCandidate("EHRD", 24),
            new JobCandidate("EHEH", 63),
            new JobCandidate("EBBR", 90),
            new JobCandidate("EGLL", 200),
        ],
        PilotRank.Trainee, count, seed);

    private static CompositeJobSource CargoAndPax(double cargo = 3, double pax = 2)
        => new((new CargoJobSource(Cfg), cargo), (new PassengerJobSource(Cfg), pax));

    [Fact]
    public void Split_SumsToCount_AndMixesBothTypes()
    {
        var jobs = CargoAndPax().Generate(Req(10, 99));

        Assert.Equal(10, jobs.Count);
        Assert.Equal(6, jobs.Count(j => j.Type == MissionType.Cargo));      // 3:2 of 10
        Assert.Equal(4, jobs.Count(j => j.Type == MissionType.Passenger));
    }

    [Fact]
    public void LargestRemainder_RoundsFairly_WhenNotDivisible()
    {
        // 3:2 of 5 → 3.0 / 2.0 exactly
        var five = CargoAndPax().Generate(Req(5, 1));
        Assert.Equal(3, five.Count(j => j.Type == MissionType.Cargo));
        Assert.Equal(2, five.Count(j => j.Type == MissionType.Passenger));

        // 3:2 of 4 → 2.4 / 1.6 → floors 2/1, the leftover seat goes to the larger remainder (passenger .6)
        var four = CargoAndPax().Generate(Req(4, 1));
        Assert.Equal(4, four.Count);
        Assert.Equal(2, four.Count(j => j.Type == MissionType.Cargo));
        Assert.Equal(2, four.Count(j => j.Type == MissionType.Passenger));
    }

    [Fact]
    public void IsDeterministic_PerSeed()
    {
        var src = CargoAndPax();
        var a = src.Generate(Req(8, 55));
        var b = src.Generate(Req(8, 55));
        Assert.Equal(
            a.Select(j => (j.Type, j.DestIcao, j.RewardCents)),
            b.Select(j => (j.Type, j.DestIcao, j.RewardCents)));
    }

    [Fact]
    public void ZeroWeightSource_IsExcluded()
    {
        var jobs = CargoAndPax(cargo: 1, pax: 0).Generate(Req(6, 3));
        Assert.Equal(6, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(MissionType.Cargo, j.Type));
    }

    [Fact]
    public void EmptyOrAllZeroWeights_Throw()
    {
        Assert.Throws<ArgumentException>(() => new CompositeJobSource());
        Assert.Throws<ArgumentException>(() => new CompositeJobSource((new CargoJobSource(Cfg), 0)));
    }
}
