using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>The executive suite (Phase 16c): the org-strength math, the incorporation gate + one-per-seat rule,
/// salary accrual, and the spine effect — a strong org lifts how well the autonomous operation reputes.</summary>
public class ExecutiveServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    private static ExecutiveService Svc(CallsignDbContext db, FakeClock clock)
        => new(db, new LedgerService(db, clock), clock, Cfg);

    private static Executive Exec(Guid companyId, ExecutiveRole role, int comp, DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), CompanyId = companyId, Role = role, Name = role.ToString(), CompetenceMilli = comp, SalaryPerDayCents = 50_000, HiredAt = now, LastPaidAt = now, IsActive = true };

    [Fact]
    public void OrgStrength_ScalesWithCompetenceAndCoverage()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cid = Guid.NewGuid();
        Assert.Equal(0, ExecutiveService.OrgStrengthMilli(Array.Empty<Executive>(), 5)); // no suite → 0

        // One 100%-competent seat of five → avg 100000 × coverage 1/5 = 20000.
        var one = new[] { Exec(cid, ExecutiveRole.ChiefOperating, 100_000, now) };
        Assert.Equal(20_000, ExecutiveService.OrgStrengthMilli(one, 5));

        // A full, 80%-competent suite → avg 80000 × coverage 1 = 80000.
        var full = Enum.GetValues<ExecutiveRole>().Select(r => Exec(cid, r, 80_000, now)).ToList();
        Assert.Equal(80_000, ExecutiveService.OrgStrengthMilli(full, 5));

        // The skill boost is the strength scaled by the config factor.
        Assert.Equal((int)Math.Round(80_000 * Cfg.ExecOrgSkillBoostFactor), ExecutiveService.OrgSkillBoostMilli(Cfg, 80_000));
    }

    [Fact]
    public async Task Hire_IsGatedOnIncorporation_AndOnePerSeat()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid cid;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Operator" }; // NOT incorporated
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            cid = c.Id;
        }
        // An un-incorporated operator can't build a C-suite.
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Svc(db, clock).HireAsync(cid, 123, ExecutiveRole.ChiefOperating));

        using (var db = tdb.NewContext()) { (await db.Companies.FindAsync(cid))!.AirlineName = "Summit Air"; await db.SaveChangesAsync(); } // incorporate

        int seed;
        using (var db = tdb.NewContext())
        {
            var market = await Svc(db, clock).GenerateMarketAsync(cid);
            var coo = market.First(m => m.Role == ExecutiveRole.ChiefOperating);
            seed = coo.Seed;
            var hired = await Svc(db, clock).HireAsync(cid, seed, ExecutiveRole.ChiefOperating);
            Assert.Equal(ExecutiveRole.ChiefOperating, hired.Role);
        }
        // The seat is filled → a second COO is refused, and the market no longer offers that seat.
        using (var db = tdb.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Svc(db, clock).HireAsync(cid, seed, ExecutiveRole.ChiefOperating));
            var market = await Svc(db, clock).GenerateMarketAsync(cid);
            Assert.DoesNotContain(market, m => m.Role == ExecutiveRole.ChiefOperating);
        }
    }

    [Fact]
    public async Task Dismiss_BooksSalaryOwed_AndDeactivates()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid cid, execId;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Summit Air", AirlineName = "Summit Air" };
            db.Companies.Add(c);
            var e = Exec(c.Id, ExecutiveRole.ChiefFinancial, 70_000, clock.UtcNow);
            e.SalaryPerDayCents = 100_000; // $1,000/day
            db.Executives.Add(e);
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, 100_000m, "start");
            cid = c.Id; execId = e.Id;
        }
        clock.UtcNow = clock.UtcNow.AddDays(3); // three days of salary owed on dismissal
        using (var db = tdb.NewContext())
            await Svc(db, clock).DismissAsync(cid, execId);

        using (var db = tdb.NewContext())
        {
            Assert.False((await db.Executives.FindAsync(execId))!.IsActive);
            var wage = await db.LedgerEntries.Where(e => e.AccountId == cid && e.Category == LedgerCategory.StaffWage).SumAsync(e => e.AmountCents);
            Assert.Equal(-3 * 100_000, wage); // 3 days × $1,000
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == cid).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, (await db.Companies.FindAsync(cid))!.CashCents); // the money invariant holds
        }
    }

    // The spine effect + salary wiring, proven end-to-end through reconcile: two identical operations, one with a
    // full competent C-suite, book the same autonomous trips — the org one reputes MORE (and pays exec salary).
    [Fact]
    public async Task Reconcile_WithOrg_LiftsOperatingReputationMoreThanWithout_AndPaysSalary()
    {
        // A large per-pass step cap so the org's higher convergence CEILING shows in a single reconcile, rather than
        // both companies being clamped to the same ±1.5-pt step while far below their (different) targets.
        var cfg = Cfg with { OperatingRepAutoMaxStepPerPassMilli = 100_000 };

        async Task<(int repDelta, long wages)> RunAsync(bool withOrg)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            Guid cid, aircraftId, staffId;
            using (var db = tdb.NewContext())
            {
                var c = new Company { Id = Guid.NewGuid(), Name = "Co", AirlineName = "Co Air" }; // incorporated
                db.Companies.Add(c);
                db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
                var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
                db.AircraftTypes.Add(type);
                var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = c.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
                db.AircraftInstances.Add(inst);
                var crew = new Staff { Id = Guid.NewGuid(), CompanyId = c.Id, Name = "Crew", Role = StaffRole.Pilot, SkillMilli = 45_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
                db.Staff.Add(crew);
                if (withOrg)
                    foreach (var r in Enum.GetValues<ExecutiveRole>())
                        db.Executives.Add(Exec(c.Id, r, 95_000, clock.UtcNow)); // a full, near-ace suite
                await db.SaveChangesAsync();
                await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, 1_000_000m, "start");
                cid = c.Id; aircraftId = inst.Id; staffId = crew.Id;
            }
            using (var db = tdb.NewContext())
                await new OperationsService(db, new LedgerService(db, clock), clock, cfg).CreateStandingOrderAsync(cid, staffId, aircraftId, "EHRD");

            clock.UtcNow = clock.UtcNow.AddDays(2);
            using (var db = tdb.NewContext())
            {
                var digest = await new OperationsService(db, new LedgerService(db, clock), clock, cfg).ReconcileAsync(cid);
                return (digest.OperatingRepDeltaMilli, digest.WagesCents);
            }
        }

        var (withOrg, withOrgWages) = await RunAsync(true);
        var (noOrg, noOrgWages) = await RunAsync(false);

        Assert.True(withOrg > 0);                 // the crew flew clean → the name grew
        Assert.True(withOrg > noOrg);             // ...and the org made it grow MORE (effective skill lifted)
        Assert.True(withOrgWages > noOrgWages);   // the org isn't free — executive salaries were paid
    }
}
