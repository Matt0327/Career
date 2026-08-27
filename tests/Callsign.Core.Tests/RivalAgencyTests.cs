using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Rival agency (Phase 16e): the route's rivals aren't a fixed backdrop — dominate a line and they
/// mobilise (RivalPressureMilli ratchets up, bleeding your share); a Network Planner keeps the pressure lower.</summary>
public class RivalAgencyTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport };

    // Pure: higher pressure strengthens rivals → your share falls; dominance sets a positive target; a Network
    // Planner scales that target down.
    [Fact]
    public void Pressure_ErodesShare_AndDefenseLowersTheTarget()
    {
        var id = Guid.NewGuid();
        int repDominant = 95_000, fareUndercut = 900;
        var fresh = RouteCompetition.Evaluate(Cfg, id, repDominant, fareUndercut, 0);
        var pressed = RouteCompetition.Evaluate(Cfg, id, repDominant, fareUndercut, 80_000);
        Assert.True(pressed.YourShareMilli < fresh.YourShareMilli); // organised rivals took share back
        Assert.Equal(fresh.RawShareMilli, pressed.RawShareMilli, 3); // underlying dominance is unchanged by pressure

        int target = RouteCompetition.PressureTarget(Cfg, id, fresh.RawShareMilli, 0.0);
        int defended = RouteCompetition.PressureTarget(Cfg, id, fresh.RawShareMilli, Cfg.NetworkPlannerCompetitionDefenseFactor);
        Assert.True(target > 0);            // dominating the line provokes the rivals
        Assert.True(defended < target);     // a Network Planner keeps that provocation down
    }

    private static async Task<int> DominateAndMeasurePressureAsync(bool withNetworkPlanner)
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid cid, routeId;
        using (var db = tdb.NewContext())
        {
            var c = new Company { Id = Guid.NewGuid(), Name = "Co", AirlineName = "Co Air", OperatingReputationMilli = 95_000 };
            db.Companies.Add(c);
            db.Airports.AddRange(A("EHAM", 52.3086, 4.7639), A("EHRD", 51.9569, 4.4372));
            var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900 };
            db.AircraftTypes.Add(type);
            var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = c.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available, HullConditionMilli = 100_000, EngineConditionMilli = 100_000 };
            db.AircraftInstances.Add(inst);
            var crew = new Staff { Id = Guid.NewGuid(), CompanyId = c.Id, Name = "Crew", Role = StaffRole.Pilot, SkillMilli = 75_000, WagePerDayCents = 20_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
            db.Staff.Add(crew);
            db.OperatingCertificates.Add(new OperatingCertificate { Id = Guid.NewGuid(), CompanyId = c.Id, Kind = CertificateKind.AirOperator, IssuedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(3650), UpdatedAt = clock.UtcNow });
            var route = new Route
            {
                Id = Guid.NewGuid(), CompanyId = c.Id, Name = "EHAM↔EHRD", OriginIcao = "EHAM", DestIcao = "EHRD", Mission = MissionType.Passenger,
                DistanceNm = 200, RoundTripHours = 2, PriceMultiplierMilli = 900, AircraftInstanceId = inst.Id, StaffId = crew.Id,
                SeatCapacity = 50, LoadFactorMilli = 800, SeatYieldCents = 20_000, RewardPerTripCents = 800_000,
                Active = true, StartedAt = clock.UtcNow, LastReconciledAt = clock.UtcNow, RivalPressureMilli = 0,
            };
            db.Routes.Add(route);
            if (withNetworkPlanner)
                db.Executives.Add(new Executive { Id = Guid.NewGuid(), CompanyId = c.Id, Role = ExecutiveRole.NetworkPlanner, Name = "Planner", CompetenceMilli = 100_000, SalaryPerDayCents = 50_000, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow, IsActive = true });
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(c.Id, LedgerCategory.StartingBalance, 5_000_000m, "start");
            cid = c.Id; routeId = route.Id;
        }
        for (int pass = 0; pass < 8; pass++) // a run of daily reconciles while you dominate the line
        {
            clock.UtcNow = clock.UtcNow.AddDays(1);
            using var db = tdb.NewContext();
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(cid);
        }
        using (var db = tdb.NewContext())
            return (await db.Routes.FindAsync(routeId))!.RivalPressureMilli;
    }

    [Fact]
    public async Task RivalPressure_RatchetsUpWhenYouDominate_AndANetworkPlannerHoldsItDown()
    {
        int unchecked_ = await DominateAndMeasurePressureAsync(withNetworkPlanner: false);
        int defended = await DominateAndMeasurePressureAsync(withNetworkPlanner: true);
        Assert.True(unchecked_ > 0);          // the rivals mobilised against a dominant carrier
        Assert.True(defended < unchecked_);   // ...but the Network Planner kept the war cooler
    }
}
