using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 11f — the scheduled-passenger network. AOC-gated top-tier routes whose per-trip revenue
/// (seats × load factor × per-seat yield) is frozen at creation and booked by the EXISTING reconcile route
/// loop — no new settlement path. Load factor rides the airline's operating reputation (the flywheel's top).</summary>
public class ScheduledServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    private static Airport A(string id, double lat, double lon, AirportKind kind = AirportKind.LargeAirport)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = kind };

    [Fact]
    public void ScheduledEconomics_Curves()
    {
        // A scheduled seat prices below a private-charter seat, and grows with distance.
        long charterSeat = Cfg.PaxPerPaxCents + (long)Math.Round(500.0 * Cfg.PaxPerPaxNmCents);
        Assert.Equal((long)Math.Round(charterSeat * Cfg.ScheduledYieldFactor), Cfg.ScheduledSeatYieldCents(500));
        Assert.True(Cfg.ScheduledSeatYieldCents(500) < charterSeat);              // cheaper per seat than charter
        Assert.True(Cfg.ScheduledSeatYieldCents(800) > Cfg.ScheduledSeatYieldCents(200)); // grows with distance

        // Load factor: a base fill at no name, rising with operating reputation, capped.
        Assert.Equal(Cfg.ScheduledBaseLoadFactorMilli, Cfg.ScheduledLoadFactorMilli(0));
        Assert.True(Cfg.ScheduledLoadFactorMilli(100_000) > Cfg.ScheduledLoadFactorMilli(0)); // a strong name fills more seats
        Assert.True(Cfg.ScheduledLoadFactorMilli(100_000) <= Cfg.ScheduledMaxLoadFactorMilli); // never a full plane
    }

    // Company + two bases + an owned airliner + a rated crew. Optionally holds a valid AOC.
    private static async Task<(Guid CompanyId, Guid AircraftId, Guid StaffId)> SeedAsync(TestDb tdb, IClock clock, int repMilli, bool holdsAoc, int seats = 50)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = repMilli };
        db.Companies.Add(company);
        db.Airports.Add(A("EHAM", 52.31, 4.76));
        db.Airports.Add(A("EGLL", 51.47, -0.46));
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsHome = true, IsActive = true });
        db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EGLL", IsActive = true });
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "DH8D", CanonicalName = "Dash 8", Category = AircraftCategory.Turboprop, CruiseKtas = 300, UsefulLoadLbs = 10_000, Seats = seats };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", Role = StaffRole.Pilot, WagePerDayCents = 30_000, SkillMilli = 80_000, IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.AddRange(type, inst, staff);
        if (holdsAoc)
            db.OperatingCertificates.Add(new OperatingCertificate { Id = Guid.NewGuid(), CompanyId = company.Id, Kind = CertificateKind.AirOperator, IssuedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(180), UpdatedAt = clock.UtcNow });
        await db.SaveChangesAsync();
        return (company.Id, inst.Id, staff.Id);
    }

    [Fact]
    public async Task CreateScheduledService_RequiresAOC()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 50_000, holdsAoc: false);
        using var db = tdb.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "LHR shuttle", "EHAM", "EGLL", aircraftId, staffId));
        Assert.Contains("Air Operator Certificate", ex.Message); // gated on the AOC (L8)
    }

    [Fact]
    public async Task CreateScheduledService_WithAOC_SetsLiveDemandBaseline()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 100_000, holdsAoc: true, seats: 50);

        Route r;
        using (var db = tdb.NewContext())
            r = await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "LHR shuttle", "EHAM", "EGLL", aircraftId, staffId);

        Assert.Equal(50, r.SeatCapacity);
        Assert.Equal(Cfg.ScheduledSeatYieldCents(r.DistanceNm), r.SeatYieldCents);   // per-seat yield still frozen
        Assert.Equal(1000, r.PriceMultiplierMilli);                                  // default fare = the market fare
        Assert.Equal(MissionType.Passenger, r.Mission);
        // Phase 14a — the at-creation load + reward are the LIVE demand baseline (current rep, the season, the fare),
        // not a permanent freeze: the reconcile pass recomputes them each time.
        double season = ScheduledDemand.SeasonMultiplier(Cfg, clock.UtcNow);
        int expectedLoad = ScheduledDemand.LoadFactorMilli(Cfg, 100_000, season, 1000);
        Assert.Equal(expectedLoad, r.LoadFactorMilli);
        Assert.Equal(ScheduledDemand.RevenuePerTripCents(50, expectedLoad, r.SeatYieldCents!.Value, 1000), r.RewardPerTripCents);
    }

    [Fact]
    public async Task CreateScheduledService_RejectsTooFewSeats()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 50_000, holdsAoc: true, seats: 8); // a light twin
        using var db = tdb.NewContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "x", "EHAM", "EGLL", aircraftId, staffId));
        Assert.Contains("airliner", ex.Message);
    }

    [Fact]
    public async Task ScheduledRoute_BooksViaExistingReconcile_LedgerExact()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 80_000, holdsAoc: true);
        using (var db = tdb.NewContext())
            await new LedgerService(db, clock).PostAsync(companyId, LedgerCategory.StartingBalance, 100_000m, "start");
        using (var db = tdb.NewContext())
            await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "LHR shuttle", "EHAM", "EGLL", aircraftId, staffId);

        clock.UtcNow = clock.UtcNow.AddDays(2);
        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.True(digest.Trips > 0);          // the scheduled network flew autonomously via the existing loop
        Assert.True(digest.GrossIncomeCents > 0);
        using (var db = tdb.NewContext())
        {
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // no new settlement path — the cash invariant holds
        }
    }

    [Fact]
    public async Task AirOperatorCertificate_IsInCatalog_GatedByStandardsBar()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
            db.Companies.Add(company);
            db.Pilots.Add(new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "A", HomeIcao = "EHAM", CurrentIcao = "EHAM", ReputationMilli = 1_000 });
            await db.SaveChangesAsync();
            companyId = company.Id;
        }
        using (var db = tdb.NewContext())
        {
            var status = await new CertificateService(db, new LedgerService(db, clock), clock).GetStatusAsync(companyId);
            var aoc = Assert.Single(status, s => s.Kind == CertificateKind.AirOperator);
            Assert.False(aoc.Held);
            Assert.False(aoc.CanApply);            // a fresh, low-rep company can't earn the AOC yet
            Assert.NotNull(aoc.Blocker);
            Assert.Equal(15_000_000, aoc.FeeCents); // the marquee fee
        }
    }

    [Fact]
    public async Task ScheduledRoute_HeldWhenAOCLapses()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 80_000, holdsAoc: true); // AOC valid 180d
        using (var db = tdb.NewContext())
            await new LedgerService(db, clock).PostAsync(companyId, LedgerCategory.StartingBalance, 1_000_000m, "start");
        using (var db = tdb.NewContext())
            await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "LHR shuttle", "EHAM", "EGLL", aircraftId, staffId);

        clock.UtcNow = clock.UtcNow.AddDays(200); // the 180-day AOC has now lapsed
        ReconcileDigest digest;
        using (var db = tdb.NewContext())
            digest = await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        Assert.Equal(0, digest.Trips); // the lapsed AOC stops the scheduled route earning (enforced every pass, not just at creation)
        Assert.NotNull(digest.CertLapsed);
        Assert.Contains(digest.CertLapsed!, s => s.Contains("Air Operator"));
    }

    [Fact]
    public async Task ScheduledRoute_FareIsRepriceable_ClampedToTheBand()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 80_000, holdsAoc: true);
        Guid routeId;
        using (var db = tdb.NewContext())
            routeId = (await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "LHR shuttle", "EHAM", "EGLL", aircraftId, staffId)).Id;

        // Phase 14a — a scheduled route's price IS a lever now (the fare), clamped to the allowed band.
        using (var db = tdb.NewContext())
        {
            var ops = new OperationsService(db, new LedgerService(db, clock), clock, Cfg);
            int stored = await ops.SetRoutePriceAsync(companyId, routeId, 1300);
            Assert.Equal(1300, stored);                            // an in-band fare stores as asked
            int clamped = await ops.SetRoutePriceAsync(companyId, routeId, 9000);
            Assert.Equal(Cfg.MaxScheduledFareMilli, clamped);       // an absurd fare clamps to the ceiling
        }
        using (var db = tdb.NewContext())
            Assert.Equal(Cfg.MaxScheduledFareMilli, (await db.Routes.FindAsync(routeId))!.PriceMultiplierMilli);
    }

    [Fact]
    public void LivingDemand_HigherFareThinsTheCabin_ButHasARevenueSweetSpot()
    {
        // Yield management: as the fare climbs, the load factor falls; revenue rises then falls, peaking near +33%.
        long RevAt(int fareMilli)
        {
            int load = ScheduledDemand.LoadFactorMilli(Cfg, 60_000, seasonMult: 1.0, fareMilli);
            return ScheduledDemand.RevenuePerTripCents(100, load, seatYieldCents: 20_000, fareMilli);
        }
        Assert.True(ScheduledDemand.LoadFactorMilli(Cfg, 60_000, 1.0, 1400) < ScheduledDemand.LoadFactorMilli(Cfg, 60_000, 1.0, 1000)); // premium thins the cabin
        long peak = RevAt(1330), below = RevAt(1000), above = RevAt(1600);
        Assert.True(peak > below);   // a modest premium out-earns the market fare
        Assert.True(peak > above);   // but too steep a fare over-thins it — the sweet spot is in between
    }

    [Fact]
    public void ReliabilityLoadMultiplier_IsBounded_AndMonotonic()
    {
        Assert.Equal(1.0, Cfg.ReliabilityLoadMultiplier(1000), 6);            // a flawless record: no drag
        Assert.Equal(Cfg.ReliabilityLoadFloor, Cfg.ReliabilityLoadMultiplier(0), 6); // a shambles: the full floor drag
        Assert.True(Cfg.ReliabilityLoadMultiplier(1000) > Cfg.ReliabilityLoadMultiplier(500));
        Assert.True(Cfg.ReliabilityLoadMultiplier(500) > Cfg.ReliabilityLoadMultiplier(0));
    }

    [Fact]
    public async Task Reliability_PoorRecord_EarnsLessThanAReliableLine()
    {
        // Phase 14c — same route, same window, same everything EXCEPT the on-time record: the unreliable line
        // fills fewer seats and earns less. Proves the reliability dampener actually bites at reconcile.
        async Task<long> Run(int startReliability)
        {
            using var tdb = new TestDb();
            var clock = new FakeClock();
            var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 80_000, holdsAoc: true);
            Guid routeId;
            using (var db = tdb.NewContext())
                routeId = (await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "s", "EHAM", "EGLL", aircraftId, staffId)).Id;
            using (var db = tdb.NewContext())
            {
                var route = await db.Routes.FindAsync(routeId);
                route!.ReliabilityMilli = startReliability;   // pre-set the record
                await db.SaveChangesAsync();
            }
            clock.UtcNow = clock.UtcNow.AddDays(2);
            using (var db = tdb.NewContext())
                return (await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId)).GrossIncomeCents;
        }

        long reliable = await Run(1000);
        long shaky = await Run(400);
        Assert.True(shaky < reliable, $"unreliable {shaky} should earn less than reliable {reliable}");
    }

    [Fact]
    public async Task Reliability_IsUpdatedByAPass_AndStaysInRange()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId) = await SeedAsync(tdb, clock, repMilli: 80_000, holdsAoc: true);
        Guid routeId;
        using (var db = tdb.NewContext())
            routeId = (await new RouteService(db, clock, Cfg).CreateScheduledServiceAsync(companyId, "s", "EHAM", "EGLL", aircraftId, staffId)).Id;

        clock.UtcNow = clock.UtcNow.AddDays(2);
        using (var db = tdb.NewContext())
            await new OperationsService(db, new LedgerService(db, clock), clock, Cfg).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
            Assert.InRange((await db.Routes.FindAsync(routeId))!.ReliabilityMilli, 0, 1000);
    }

    [Fact]
    public void LivingDemand_LoadFactor_IsHardBounded_AndRidesReputation()
    {
        // Pump-safe: the load factor never escapes its band whatever the inputs, and a stronger name fills more.
        Assert.True(ScheduledDemand.LoadFactorMilli(Cfg, 100_000, 2.0, 700) <= Cfg.ScheduledMaxLoadFactorMilli);   // even a discount in a boom is capped
        Assert.True(ScheduledDemand.LoadFactorMilli(Cfg, 0, 0.5, 1600) >= Cfg.ScheduledMinLoadFactorMilli);        // even a weak name in a slump has a floor
        Assert.True(ScheduledDemand.LoadFactorMilli(Cfg, 90_000, 1.0, 1000) > ScheduledDemand.LoadFactorMilli(Cfg, 10_000, 1.0, 1000)); // reputation fills seats
    }
}
