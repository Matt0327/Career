using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class JobCancelTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static async Task<(Guid CompanyId, Guid AsgId)> SeedAcceptedAsync(CallsignDbContext db, string? clientKey, int loyalty)
    {
        var companyId = Guid.NewGuid();
        db.Companies.Add(new Company { Id = companyId, Name = "Test Co" });
        if (clientKey is not null)
            db.Clients.Add(new Client { Id = Guid.NewGuid(), CompanyId = companyId, ClientKey = clientKey, Name = "Acme", HomeIcao = "EGKB", LoyaltyMilli = loyalty });
        var asg = new JobAssignment
        {
            Id = Guid.NewGuid(), JobId = Guid.NewGuid(), AccountId = companyId, PilotId = Guid.NewGuid(),
            Type = MissionType.Cargo, OriginIcao = "EGKB", DestIcao = "EGTO", Commodity = "Machine parts", WeightLbs = 400, DistanceNm = 30,
            RewardQuoteCents = 100_000, XpQuote = 10, ClientKey = clientKey, ClientName = clientKey is null ? null : "Acme",
            Status = AssignmentStatus.Accepted,
        };
        db.JobAssignments.Add(asg);
        await db.SaveChangesAsync();
        return (companyId, asg.Id);
    }

    [Fact]
    public async Task Cancel_AbandonsTheJob_AndNicksClientLoyalty()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, asgId;
        await using (var db = tdb.NewContext())
            (companyId, asgId) = await SeedAcceptedAsync(db, "client:acme", 10_000);

        await using (var db = tdb.NewContext())
        {
            var svc = new JobAssignmentService(db, clock, Cfg);
            var (clientName, lost) = await svc.CancelAsync(asgId, companyId);
            Assert.Equal("Acme", clientName);
            Assert.Equal(-Cfg.ClientLoyaltyCancelMilli, lost); // lost is positive milli removed
        }

        await using (var db = tdb.NewContext())
        {
            Assert.Equal(AssignmentStatus.Abandoned, (await db.JobAssignments.FirstAsync(a => a.Id == asgId)).Status);
            Assert.Equal(10_000 + Cfg.ClientLoyaltyCancelMilli, (await db.Clients.FirstAsync()).LoyaltyMilli); // 10000 - 400
        }
    }

    [Fact]
    public async Task Cancel_LoyaltyNeverGoesNegative()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, asgId;
        await using (var db = tdb.NewContext())
            (companyId, asgId) = await SeedAcceptedAsync(db, "client:new", 100); // barely any loyalty

        await using (var db = tdb.NewContext())
            await new JobAssignmentService(db, clock, Cfg).CancelAsync(asgId, companyId);

        await using (var db = tdb.NewContext())
            Assert.Equal(0, (await db.Clients.FirstAsync()).LoyaltyMilli); // clamped, not negative
    }

    [Fact]
    public async Task Cancel_RefusesAnAlreadyAbandonedJob()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId, asgId;
        await using (var db = tdb.NewContext())
            (companyId, asgId) = await SeedAcceptedAsync(db, null, 0);

        await using (var db = tdb.NewContext())
            await new JobAssignmentService(db, clock, Cfg).CancelAsync(asgId, companyId);

        await using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new JobAssignmentService(db, clock, Cfg).CancelAsync(asgId, companyId));
    }
}
