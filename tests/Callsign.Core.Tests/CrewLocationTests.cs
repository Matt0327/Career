using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 12 — crew location realism: hired pilots live at a field, must be co-located with the aircraft
/// they crew, are repositioned for a fee, and can be let go. Plus the aircraft ferry's adequate-runway guard and
/// the shared airport-suitability predicates.</summary>
public class CrewLocationTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    // Amsterdam / Rotterdam / Eindhoven — real spacing so distances are meaningful.
    private const double EhamLat = 52.3086, EhamLon = 4.7639;
    private const double EhrdLat = 51.9569, EhrdLon = 4.4372;
    private const double EhehLat = 51.4501, EhehLon = 5.3745;

    private static Airport A(string id, double lat, double lon, AirportKind kind = AirportKind.LargeAirport,
        int? longestRunwayFt = 10_000, string? icaoCode = "__self__")
        => new() { Ident = id, IcaoCode = icaoCode == "__self__" ? id : icaoCode, Name = id, Latitude = lat, Longitude = lon, Kind = kind, LongestRunwayFt = longestRunwayFt };

    private static (Company, AircraftType, AircraftInstance) Fleet(string atIcao)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900, MinRunwayFt = 1500 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = atIcao, Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        return (company, type, inst);
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // matches FakeClock's default

    private static Staff Crew(Guid companyId, string? currentIcao, int skill = 90_000)
        => new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Test Pilot", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = skill, CurrentIcao = currentIcao, IsActive = true, HiredAt = T0, LastPaidAt = T0 };

    private static OperationsService Ops(CallsignDbContext db, FakeClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    // ── Hire places the pilot where you recruit them ──────────────────────────────────────────────

    [Fact]
    public async Task Hire_PositionsCrewAtTheRecruitingField()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        using (var db = tdb.NewContext()) { db.Companies.Add(company); await db.SaveChangesAsync(); }
        using (var db = tdb.NewContext())
        {
            var staff = await Ops(db, clock).HireAsync(company.Id, 123, "EHAM");
            Assert.Equal("EHAM", staff.CurrentIcao);
        }
    }

    // ── Co-location gate on assignment ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assign_BlockedWhenCrewIsAtAnotherField()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, type, inst) = Fleet("EHAM");
        var crew = Crew(company.Id, "EHRD"); // pilot is at Rotterdam, the aircraft is at Amsterdam
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.Add(crew);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Ops(db, clock).CreateStandingOrderAsync(company.Id, crew.Id, inst.Id, "EHRD"));
            Assert.Contains("EHRD", ex.Message); // names where the pilot is / where to reposition
        }
    }

    [Fact]
    public async Task Assign_Succeeds_WhenCoLocated_AndPositionsAGrandfatheredCrew()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, type, inst) = Fleet("EHAM");
        var crew = Crew(company.Id, null); // un-positioned legacy crew → grandfathered in, then placed at the origin
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.Add(crew);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
            await Ops(db, clock).CreateStandingOrderAsync(company.Id, crew.Id, inst.Id, "EHRD");
        using (var db = tdb.NewContext())
            Assert.Equal("EHAM", (await db.Staff.FindAsync(crew.Id))!.CurrentIcao); // now operating from the origin
    }

    // ── Reposition (deadhead) for a fee ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RelocateCrew_MovesThem_AndChargesTheDeadheadFee()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, _, _) = Fleet("EHAM");
        var crew = Crew(company.Id, "EHAM");
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.Staff.Add(crew);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        long fee;
        using (var db = tdb.NewContext())
            fee = await Ops(db, clock).RelocateCrewAsync(company.Id, crew.Id, "EHRD");

        using (var db = tdb.NewContext())
        {
            double dist = Callsign.Core.Geo.GeoMath.DistanceNm(EhamLat, EhamLon, EhrdLat, EhrdLon);
            long expected = Cfg.CrewPositionBaseCents + (long)Math.Round(dist * Cfg.CrewPositionPerNmCents);
            Assert.Equal(expected, fee);
            Assert.Equal("EHRD", (await db.Staff.FindAsync(crew.Id))!.CurrentIcao);
            var entry = await db.LedgerEntries.SingleAsync(e => e.Category == LedgerCategory.CrewPositioning);
            Assert.Equal(-fee, entry.AmountCents); // a debit for exactly the fee
            var company2 = await db.Companies.FindAsync(company.Id);
            var ledgerSum = await db.LedgerEntries.SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company2!.CashCents); // cash invariant holds
        }
    }

    [Fact]
    public async Task RelocateCrew_IsIdempotentPerKey()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, _, _) = Fleet("EHAM");
        var crew = Crew(company.Id, "EHAM");
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.Staff.Add(crew);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        using (var db = tdb.NewContext()) await Ops(db, clock).RelocateCrewAsync(company.Id, crew.Id, "EHRD", "move-1");
        using (var db = tdb.NewContext()) await Ops(db, clock).RelocateCrewAsync(company.Id, crew.Id, "EHRD", "move-1");
        using (var db = tdb.NewContext())
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CrewPositioning)); // billed once
    }

    [Fact]
    public async Task RelocateCrew_RejectsAPilotFlyingALine_AndAnUnsuitableField()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, type, inst) = Fleet("EHAM");
        var crew = Crew(company.Id, "EHAM");
        var busy = Crew(company.Id, "EHAM");
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.AddRange(crew, busy);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon),
                A("EHHELI", EhrdLat + 0.01, EhrdLon, AirportKind.Heliport)); // a heliport — crew can't position there
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        using (var db = tdb.NewContext()) await Ops(db, clock).CreateStandingOrderAsync(company.Id, busy.Id, inst.Id, "EHRD");

        using (var db = tdb.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).RelocateCrewAsync(company.Id, busy.Id, "EHRD")); // committed
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).RelocateCrewAsync(company.Id, crew.Id, "EHHELI")); // not landable
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).RelocateCrewAsync(company.Id, crew.Id, "ZZZZ")); // unknown
        }
    }

    // ── Dismiss (fire) stops the wage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_Deactivates_StopsWages_AndRefusesAPilotOnALine()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, type, inst) = Fleet("EHAM");
        var idle = Crew(company.Id, "EHAM");
        var flying = Crew(company.Id, "EHAM");
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.AddRange(idle, flying);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        using (var db = tdb.NewContext()) await Ops(db, clock).CreateStandingOrderAsync(company.Id, flying.Id, inst.Id, "EHRD");

        clock.UtcNow = clock.UtcNow.AddDays(3); // wages accrue for the idle pilot before we let them go
        using (var db = tdb.NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DismissAsync(company.Id, flying.Id)); // on a line
            await Ops(db, clock).DismissAsync(company.Id, idle.Id); // idle → let go (settles the final wage first)
        }
        int wagesAtDismiss;
        using (var db = tdb.NewContext())
        {
            Assert.False((await db.Staff.FindAsync(idle.Id))!.IsActive);
            wagesAtDismiss = await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.StaffWage && e.StaffId == idle.Id);
            Assert.True(wagesAtDismiss >= 1); // the final accrued wage IS billed on dismiss — not forfeited (fairness)
        }

        clock.UtcNow = clock.UtcNow.AddDays(3); // more time passes
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(company.Id);
        using (var db = tdb.NewContext())
        {
            var wagesAfter = await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.StaffWage && e.StaffId == idle.Id);
            Assert.Equal(wagesAtDismiss, wagesAfter); // and NOTHING accrues after they've left — the leak is closed
        }
    }

    // ── Regression (review MED): cancel must leave the crew AT the origin, even a non-'suitable' one ──

    [Fact]
    public async Task CancelStandingOrder_LeavesCrewAtANonSuitableOrigin_WithoutBouncingThem()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900, MinRunwayFt = 1500 };
        // The aircraft sits at a field with NO real ICAO (a local-code strip) — an unsuitable origin. The crew
        // must be returned HERE on cancel, not bounced to a nearby 'suitable' field (which would strand them).
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "NL-0099", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        var crew = Crew(company.Id, "NL-0099");
        Guid orderId;
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.Add(crew);
            db.Airports.AddRange(
                A("NL-0099", EhamLat, EhamLon, AirportKind.SmallAirport, icaoCode: null), // landable but no real ICAO → not "suitable"
                A("EHRD", EhrdLat, EhrdLon), A("EHAM", EhamLat + 0.02, EhamLon)); // nearby suitable fields it must NOT bounce to
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        using (var db = tdb.NewContext())
            orderId = (await Ops(db, clock).CreateStandingOrderAsync(company.Id, crew.Id, inst.Id, "EHRD")).Id;
        using (var db = tdb.NewContext()) await Ops(db, clock).CancelStandingOrderAsync(company.Id, orderId);
        using (var db = tdb.NewContext())
            Assert.Equal("NL-0099", (await db.Staff.FindAsync(crew.Id))!.CurrentIcao); // stayed at the origin — no bounce, no soft-lock
    }

    // ── Cancel leaves the freed pilot at the line's origin ────────────────────────────────────────

    [Fact]
    public async Task CancelStandingOrder_LeavesTheFreedPilotAtTheOrigin()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (company, type, inst) = Fleet("EHAM");
        var crew = Crew(company.Id, "EHAM");
        Guid orderId;
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(type); db.AircraftInstances.Add(inst); db.Staff.Add(crew);
            db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 100_000m, "start");
        }
        using (var db = tdb.NewContext())
            orderId = (await Ops(db, clock).CreateStandingOrderAsync(company.Id, crew.Id, inst.Id, "EHRD")).Id;
        using (var db = tdb.NewContext()) await Ops(db, clock).CancelStandingOrderAsync(company.Id, orderId);
        using (var db = tdb.NewContext())
            Assert.Equal("EHAM", (await db.Staff.FindAsync(crew.Id))!.CurrentIcao); // available at the origin
    }

    // ── The aircraft ferry now checks the destination runway ──────────────────────────────────────

    [Fact]
    public async Task Ferry_RejectsARunwayTooShortForTheAircraft_ButAllowsAnAdequateOne()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        var jet = new AircraftType { Id = Guid.NewGuid(), Key = "A20N", CanonicalName = "A320neo", Category = AircraftCategory.Jet, CruiseKtas = 450, MinRunwayFt = 6500 };
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = jet.Id, CompanyId = company.Id, Tail = "CS-9", LocationIcao = "EHAM", Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available };
        using (var db = tdb.NewContext())
        {
            db.Companies.Add(company); db.AircraftTypes.Add(jet); db.AircraftInstances.Add(inst);
            db.Airports.AddRange(
                A("EHAM", EhamLat, EhamLon, longestRunwayFt: 11_000),
                A("TINY", EhrdLat, EhrdLon, longestRunwayFt: 3_000),   // too short for a 6500 ft jet
                A("BIGG", EhehLat, EhehLon, longestRunwayFt: 9_000));  // adequate
            await db.SaveChangesAsync();
            await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 10_000_000m, "start");
        }
        using (var db = tdb.NewContext())
        {
            var dealer = new AircraftDealerService(db, new LedgerService(db, clock), clock, Cfg);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dealer.RelocateAsync(company.Id, inst.Id, "TINY"));
            Assert.Contains("too short", ex.Message);
            await dealer.RelocateAsync(company.Id, inst.Id, "BIGG"); // adequate → succeeds
        }
        using (var db = tdb.NewContext())
            Assert.Equal("BIGG", (await db.AircraftInstances.FindAsync(inst.Id))!.LocationIcao);
    }

    // ── The shared suitability predicates + nearest-suitable ──────────────────────────────────────

    [Fact]
    public void Suitability_Predicates_HandleKind_Icao_AndRunway()
    {
        Assert.True(AirportSuitability.IsSuitable(A("EHAM", EhamLat, EhamLon, longestRunwayFt: 8_000)));
        Assert.False(AirportSuitability.IsSuitable(A("EHHELI", EhamLat, EhamLon, AirportKind.Heliport))); // not landable
        Assert.False(AirportSuitability.IsSuitable(A("NL-0099", EhamLat, EhamLon, icaoCode: null)));      // no real ICAO
        Assert.False(AirportSuitability.IsSuitable(A("SHRT", EhamLat, EhamLon, longestRunwayFt: 1_000), minRunwayFt: 6500)); // too short
        Assert.True(AirportSuitability.IsSuitable(A("UNKR", EhamLat, EhamLon, longestRunwayFt: null), minRunwayFt: 6500));   // unknown runway → not rejected
        Assert.True(AirportSuitability.RunwayAdequate(A("H", 0, 0, longestRunwayFt: 500), minRunwayFt: 0));  // no requirement (helicopter)
    }

    [Fact]
    public async Task NearestSuitable_SkipsUnsuitable_AndReturnsTheNearestUsableField()
    {
        using var tdb = new TestDb();
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(
                A("HELI", EhamLat + 0.001, EhamLon, AirportKind.Heliport),        // closest, but not landable
                A("EHRD", EhrdLat, EhrdLon, longestRunwayFt: 9_000),              // ~20 nm, suitable
                A("EHEH", EhehLat, EhehLon, longestRunwayFt: 9_000));             // further, suitable
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var near = await new AirportRepository(db).NearestSuitableAsync(EhamLat, EhamLon, null, 300);
            Assert.NotNull(near);
            Assert.Equal("EHRD", near!.Value.Airport.Ident); // the heliport is skipped; nearest suitable is Rotterdam
        }
    }
}
