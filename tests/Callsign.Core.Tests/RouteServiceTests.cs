using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class RouteServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static RouteService Routes(CallsignDbContext db, IClock clock) => new(db, clock, Cfg);
    private static OperationsService Ops(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    private static async Task<(Guid CompanyId, Guid AircraftId, Guid StaffId)> SeedAsync(TestDb tdb, IClock clock, bool bothBases = true)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.Airports.Add(new Airport { Ident = "EHAM", IcaoCode = "EHAM", Name = "Schiphol", Latitude = 52.31, Longitude = 4.76, Kind = AirportKind.LargeAirport });
        db.Airports.Add(new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = 51.95, Longitude = 4.44, Kind = AirportKind.MediumAirport });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
        if (bothBases)
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHRD", IsActive = true });
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172", Category = AircraftCategory.LightSingle, CruiseKtas = 120, UsefulLoadLbs = 900 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Availability = AircraftAvailability.Available };
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 60_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.AddRange(type, inst, staff);
        await db.SaveChangesAsync();
        return (company.Id, inst.Id, staff.Id);
    }

    [Fact]
    public async Task Create_BetweenBases_FreezesReward_AndReservesAircraft()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
        {
            var r = await Routes(db, clock).CreateRouteAsync(companyId, "NL Freight", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);
            Assert.True(r.RewardPerTripCents > 0);
            Assert.True(r.DistanceNm > 0 && r.RoundTripHours > 0);
            Assert.Equal(MissionType.Cargo, r.Mission);
        }
        using (var db = tdb.NewContext())
            Assert.Equal(AircraftAvailability.Reserved, (await db.AircraftInstances.FindAsync(aircraftId))!.Availability);
    }

    [Fact]
    public async Task Create_NonBaseEndpoint_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, bothBases: false); // EHRD isn't a base
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo));
    }

    [Fact]
    public async Task Create_ReputationOrIllicitMission_Throws()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Illicit));
    }

    [Fact]
    public async Task Create_CertificateGatedMission_WithoutCert_Throws()
    {
        // Phase 8e regression: a scheduled route is a second path to premium work and must honour the same
        // certificate gate as the manual accept path — no fee-free VIP/Hazardous route without the licence.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Vip));
        Assert.Empty(await db.Routes.ToListAsync()); // nothing created
    }

    [Fact]
    public async Task Create_CertificateGatedMission_WithValidCert_Succeeds()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
        {
            db.OperatingCertificates.Add(new OperatingCertificate
            {
                Id = Guid.NewGuid(), CompanyId = companyId, Kind = CertificateKind.Charter,
                IssuedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(120), UpdatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var r = await Routes(db, clock).CreateRouteAsync(companyId, "VIP line", "EHAM", "EHRD", aircraftId, staffId, MissionType.Vip);
            Assert.Equal(MissionType.Vip, r.Mission);
        }
    }

    [Fact]
    public async Task Reconcile_HoldsPremiumRoute_WhenCertificateLapsed_NoIncome_ForfeitsWindow()
    {
        // Phase 8e regression: a VIP route set up while certified must STOP paying once the cert lapses — the
        // reconcile loop holds it, books no income, and forfeits the unauthorised window (watermark -> now) so
        // the lapse can't be banked and reaped with one cheap renewal.
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
        {
            // A route whose Charter cert has since lapsed (none present now); 48h of trips would otherwise accrue.
            db.Routes.Add(new Route
            {
                Id = Guid.NewGuid(), CompanyId = companyId, Name = "VIP line", OriginIcao = "EHAM", DestIcao = "EHRD",
                Mission = MissionType.Vip, DistanceNm = 30, RoundTripHours = 1, RewardPerTripCents = 500_000, PriceMultiplierMilli = 1000,
                AircraftInstanceId = aircraftId, StaffId = staffId, Active = true,
                StartedAt = clock.UtcNow.AddHours(-48), LastReconciledAt = clock.UtcNow.AddHours(-48), UpdatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await Ops(db, clock).ReconcileAsync(companyId);

        Assert.Contains(digest.CertLapsed!, s => s.Contains("VIP line")); // surfaced (Law 4)
        using (var db = tdb.NewContext())
        {
            Assert.Empty(await db.LedgerEntries.Where(e => e.Category == LedgerCategory.JobPayout).ToListAsync()); // no premium paid
            var r = await db.Routes.FirstAsync();
            Assert.Equal(clock.UtcNow, r.LastReconciledAt); // window forfeited, not banked
        }
    }

    [Fact]
    public async Task Reconcile_BooksRouteTrips_FeeFree()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext())
            await Routes(db, clock).CreateRouteAsync(companyId, "NL Freight", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo);

        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
        {
            var digest = await Ops(db, clock).ReconcileAsync(companyId);
            Assert.True(digest.Trips > 0);
            Assert.True(digest.GrossIncomeCents > 0);
            Assert.Equal(0, digest.FeesCents); // both ends are your bases — no landing fees
        }
        using (var db = tdb.NewContext())
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Description.StartsWith("Route"));
    }

    [Fact]
    public async Task Cancel_FreesAircraft()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        Guid routeId;
        using (var db = tdb.NewContext())
            routeId = (await Routes(db, clock).CreateRouteAsync(companyId, "x", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo)).Id;
        using (var db = tdb.NewContext())
            await Routes(db, clock).CancelRouteAsync(companyId, routeId);
        using (var db = tdb.NewContext())
            Assert.Equal(AircraftAvailability.Available, (await db.AircraftInstances.FindAsync(aircraftId))!.Availability);
    }

    [Fact]
    public async Task CreateRoute_ClampsMarkup_AndRepriceClampsToo()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
        Guid routeId;
        using (var db = tdb.NewContext())
        {
            var r = await Routes(db, clock).CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo, 9_000); // absurd markup
            routeId = r.Id;
            Assert.Equal(Cfg.MaxContractMarkupMilli, r.PriceMultiplierMilli); // clamped to the cap
        }
        using (var db = tdb.NewContext())
        {
            int m = await Ops(db, clock).SetRoutePriceAsync(companyId, routeId, 500); // below fair → clamps up to fair
            Assert.Equal(1000, m);
            Assert.Equal(1000, (await db.Routes.FindAsync(routeId))!.PriceMultiplierMilli);
        }
    }

    [Fact]
    public async Task Reprice_BooksPendingTripsAtOldPrice_NotRetroactively()
    {
        // A route reprice must reconcile FIRST at the OLD rate; trips already flown are never re-priced up.
        // Compared against a plain reconcile over the same window, a reprice-to-+50% books the SAME payout.
        var cfg = Cfg with { BaseIncidentRatePct = 0.0 }; // no incidents → income is exactly filled × price

        async Task<long> RunAsync(bool reprice)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock);
            Guid routeId;
            using (var db = tdb.NewContext())
                routeId = (await new RouteService(db, clock, cfg)
                    .CreateRouteAsync(companyId, "R", "EHAM", "EHRD", aircraftId, staffId, MissionType.Cargo)).Id; // fair rate (1000)

            clock.UtcNow = clock.UtcNow.AddDays(3); // trips accrue at the fair rate

            using (var db = tdb.NewContext())
            {
                var ops = new OperationsService(db, new LedgerService(db, clock), clock, cfg);
                if (reprice) await ops.SetRoutePriceAsync(companyId, routeId, 1500); // reconciles-first, THEN marks up
                else await ops.ReconcileAsync(companyId);
            }
            using (var db = tdb.NewContext())
                return await db.LedgerEntries.Where(e => e.Category == LedgerCategory.JobPayout).SumAsync(e => e.AmountCents);
        }

        long control = await RunAsync(reprice: false);
        long repriced = await RunAsync(reprice: true);
        Assert.True(control > 0);           // trips actually accrued and paid out
        Assert.Equal(control, repriced);    // repricing booked the pending trips at the OLD fair rate, not +50%
    }
}
