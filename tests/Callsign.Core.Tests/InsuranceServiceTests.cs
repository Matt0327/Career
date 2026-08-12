using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class InsuranceServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static InsuranceService Ins(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);
    private static OperationsService Ops(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    private static async Task<(Guid CompanyId, Guid AircraftId)> SeedAsync(TestDb tdb, IClock clock, int conditionMilli = 100_000)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, Seats = 4, UsefulLoadLbs = 900, CruiseKtas = 120 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", HullConditionMilli = conditionMilli, EngineConditionMilli = conditionMilli };
        db.AddRange(company, type, inst);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        return (company.Id, inst.Id);
    }

    [Fact]
    public async Task Insure_CreatesPolicy_WithComputedPremiumAndPayout()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId) = await SeedAsync(tdb, clock);
        using var db = tdb.NewContext();
        var p = await Ins(db, clock).InsureAsync(companyId, aircraftId, null);
        Assert.Equal(Cfg.InsuranceDefaultCoverageMilli, p.CoverageMilli);
        Assert.True(p.PremiumPerWeekCents > 0);
        Assert.True(p.CoveredValueCents > 0);
        Assert.Equal(p.CoveredValueCents - p.DeductibleCents, p.ClaimPayoutCents);
    }

    [Fact]
    public async Task Insure_Twice_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Ins(db, clock).InsureAsync(companyId, aircraftId, null);
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ins(db, clock).InsureAsync(companyId, aircraftId, null));
    }

    [Fact]
    public async Task Reconcile_BillsPremium_OverElapsedDays()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Ins(db, clock).InsureAsync(companyId, aircraftId, null);

        clock.UtcNow = clock.UtcNow.AddDays(14);
        using (var db = tdb.NewContext())
            Assert.True((await Ops(db, clock).ReconcileAsync(companyId)).InsuranceCents > 0);
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.InsurancePremium);
    }

    [Fact]
    public async Task Claim_OnTotalLoss_PaysOut_AndWritesOffTheAirframe()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId) = await SeedAsync(tdb, clock);
        Guid policyId;
        using (var db = tdb.NewContext())
            policyId = (await Ins(db, clock).InsureAsync(companyId, aircraftId, null)).Id;

        using (var db = tdb.NewContext()) // wreck it below the write-off threshold
        {
            var inst = await db.AircraftInstances.FindAsync(aircraftId);
            inst!.HullConditionMilli = 10_000;
            inst.EngineConditionMilli = 10_000;
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
            Assert.True(await Ins(db, clock).ClaimAsync(companyId, policyId) > 0);
        using (var db = tdb.NewContext())
        {
            Assert.True((await db.AircraftInstances.FindAsync(aircraftId))!.IsDeleted); // written off
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.InsuranceClaim && e.AmountCents > 0);
            Assert.False((await db.InsurancePolicies.FindAsync(policyId))!.Active);
        }
    }

    [Fact]
    public async Task Claim_OnHealthyAircraft_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId) = await SeedAsync(tdb, clock, conditionMilli: 100_000);
        Guid policyId;
        using (var db = tdb.NewContext())
            policyId = (await Ins(db, clock).InsureAsync(companyId, aircraftId, null)).Id;
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ins(db, clock).ClaimAsync(companyId, policyId));
    }
}
