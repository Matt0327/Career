using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 16f ratchet-only rail: a C-suite you can't afford is furloughed — unpaid and their effects
/// pause — so an over-large org stalls the operation instead of bleeding you into ruin. Warned, recoverable.</summary>
public class ExecutiveFurloughTests
{
    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    private static async Task<(int repDelta, IReadOnlyList<string> warnings)> RunAsync(bool solvent)
    {
        // A high delinquency floor makes an ordinarily-funded company read as "can't afford the C-suite" unless we
        // top it up; a big rep step-cap lets the org's (present-or-furloughed) boost show in a single pass.
        var cfg = EconomyConfig.Default with { LoanDelinquencyCashFloorCents = 100_000_000, OperatingRepAutoMaxStepPerPassMilli = 100_000 };
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid cid, crewId, aircraftId;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Co", AirlineName = "Co Air" };
            db.Companies.Add(c);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = c.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, HullConditionMilli = 100_000, EngineConditionMilli = 100_000 };
            db.AircraftInstances.Add(inst);
            var crew = new Staff { Id = Guid.NewGuid(), CompanyId = c.Id, Name = "Crew", Role = StaffRole.Pilot, SkillMilli = 45_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(crew);
            foreach (var r in Enum.GetValues<ExecutiveRole>())
                db.Executives.Add(new Executive { Id = Guid.NewGuid(), CompanyId = c.Id, Role = r, Name = r.ToString(), CompetenceMilli = 95_000, SalaryPerDayCents = 50_000, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow, IsActive = true });
            await db.SaveChangesAsync();
            // Solvent: well above the $1,000,000 floor. Broke: a token balance, below it.
            await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, solvent ? 5_000_000m : 5_000m, "seed");
            cid = c.Id; crewId = crew.Id; aircraftId = inst.Id;
        }
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, cfg).CreateStandingOrderAsync(cid, crewId, aircraftId, "EHRD");
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
        {
            var d = await new OperationsService(db, new LedgerService(db, clock), clock, cfg).ReconcileAsync(cid);
            return (d.OperatingRepDeltaMilli, d.LoanWarnings ?? new List<string>());
        }
    }

    [Fact]
    public async Task Furlough_WhenBroke_PausesTheOrgAndWarns_ButPaysWhenSolvent()
    {
        var (brokeRep, brokeWarn) = await RunAsync(solvent: false);
        var (solventRep, solventWarn) = await RunAsync(solvent: true);

        Assert.Contains(brokeWarn, w => w.Contains("furlough", StringComparison.OrdinalIgnoreCase)); // warned, not silently wiped
        Assert.DoesNotContain(solventWarn, w => w.Contains("furlough", StringComparison.OrdinalIgnoreCase));
        Assert.True(solventRep > brokeRep); // the org's boost applies only when it's paid — furloughed, the operation stalls to bare crew skill
    }
}
