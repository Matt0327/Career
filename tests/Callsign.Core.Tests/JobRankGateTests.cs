using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class JobRankGateTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, PilotRank.Trainee)]
    [InlineData(89, PilotRank.Trainee)]
    [InlineData(90, PilotRank.Copilot)]
    [InlineData(179, PilotRank.Copilot)]
    [InlineData(180, PilotRank.Captain)]
    [InlineData(299, PilotRank.Captain)]
    [InlineData(300, PilotRank.SeniorCaptain)]
    [InlineData(400, PilotRank.SeniorCaptain)]
    public void RankForDistance_Bands(double nm, PilotRank expected)
        => Assert.Equal(expected, Cfg.RankForDistance(nm));

    [Fact]
    public void Generators_StampRankByDistance()
    {
        var req = new JobGenerationRequest("EHAM",
            [new JobCandidate("EHRD", 24), new JobCandidate("EGLL", 200)],
            PilotRank.Trainee, Count: 8, Seed: 5);
        Assert.All(new CargoJobSource(Cfg).Generate(req), j => Assert.Equal(Cfg.RankForDistance(j.DistanceNm), j.RequiredRank));
        Assert.All(new PassengerJobSource(Cfg).Generate(req), j => Assert.Equal(Cfg.RankForDistance(j.DistanceNm), j.RequiredRank));
    }

    private static async Task<(Guid CompanyId, Guid PilotId, Guid JobId)> SeedAsync(TestDb tdb, PilotRank pilotRank, PilotRank jobRank)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "P", HomeIcao = "EHAM", CurrentIcao = "EHAM", Rank = pilotRank };
        var job = new Job
        {
            Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EGLL",
            Commodity = "Machine parts", WeightLbs = 100, RewardCents = 100_000, Xp = 10, DistanceNm = 200,
            RequiredRank = jobRank, GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
        };
        db.AddRange(company, pilot, job);
        await db.SaveChangesAsync();
        return (company.Id, pilot.Id, job.Id);
    }

    [Fact]
    public async Task Accept_AboveRank_Throws_WithReason()
    {
        using var tdb = new TestDb();
        var (companyId, pilotId, jobId) = await SeedAsync(tdb, PilotRank.Trainee, PilotRank.Captain);
        using var db = tdb.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new JobAssignmentService(db, new FakeClock()).AcceptAsync(jobId, companyId, pilotId));
        Assert.Contains("Captain", ex.Message);      // names the required rank
        Assert.NotEmpty(await db.Jobs.ToListAsync()); // refused: the job stays on the board
    }

    [Fact]
    public async Task Accept_AtRequiredRank_Succeeds()
    {
        using var tdb = new TestDb();
        var (companyId, pilotId, jobId) = await SeedAsync(tdb, PilotRank.Captain, PilotRank.Captain);
        using var db = tdb.NewContext();
        var a = await new JobAssignmentService(db, new FakeClock()).AcceptAsync(jobId, companyId, pilotId);
        Assert.Equal(AssignmentStatus.Accepted, a.Status);
    }
}
