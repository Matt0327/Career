using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 12 — crew dispatch: hand a board JOB to a hired crew to fly autonomously as a one-way leg. It
/// settles through the reconcile engine, relocates the aircraft + crew to the destination, and a RENTAL is allowed
/// (its usage fee bills the flown hours, keeping it pump-safe).</summary>
public class CrewDispatchTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private const double EhamLat = 52.3086, EhamLon = 4.7639;
    private const double EhrdLat = 51.9569, EhrdLon = 4.4372;
    private const double EhehLat = 51.4501, EhehLon = 5.3745;

    private static Airport A(string id, double lat, double lon)
        => new() { Ident = id, IcaoCode = id, Name = id, Latitude = lat, Longitude = lon, Kind = AirportKind.LargeAirport, LongestRunwayFt = 10_000 };

    private static OperationsService Ops(CallsignDbContext db, FakeClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);

    // company + owned C172 at EHAM + a 90%-skill crew at EHAM + a board job EHAM→EHRD (300 nm). Returns ids + jobId.
    private static async Task<(Guid CompanyId, Guid AircraftId, Guid StaffId, Guid JobId)> SeedAsync(
        TestDb tdb, FakeClock clock, long jobReward = 200_000, long? hubBonus = null, OwnershipKind ownership = OwnershipKind.Owned,
        MissionType jobType = MissionType.Cargo, PilotRank jobRank = PilotRank.Trainee, PilotRank pilotRank = PilotRank.Captain, int pilotRepMilli = 50_000)
    {
        using var db = tdb.NewContext();
        var company = new Company { Id = Guid.NewGuid(), Name = "Co" };
        db.Companies.Add(company);
        db.Pilots.Add(new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Player", HomeIcao = "EHAM", CurrentIcao = "EHAM", Rank = pilotRank, ReputationMilli = pilotRepMilli });
        db.Airports.AddRange(A("EHAM", EhamLat, EhamLon), A("EHRD", EhrdLat, EhrdLon));
        var type = new AircraftType { Id = Guid.NewGuid(), Key = "C172", CanonicalName = "C172", Category = AircraftCategory.LightSingle, CruiseKtas = 150, UsefulLoadLbs = 900, Seats = 4 };
        db.AircraftTypes.Add(type);
        var inst = new AircraftInstance { Id = Guid.NewGuid(), TypeId = type.Id, CompanyId = company.Id, Tail = "CS-1", LocationIcao = "EHAM", Ownership = ownership, Availability = AircraftAvailability.Available };
        db.AircraftInstances.Add(inst);
        var staff = new Staff { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Sky Pilot", Role = StaffRole.Pilot, WagePerDayCents = 20_000, SkillMilli = 90_000, CurrentIcao = "EHAM", IsActive = true, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow };
        db.Staff.Add(staff);
        var job = new Job
        {
            Id = Guid.NewGuid(), Type = jobType, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Mail",
            WeightLbs = 400, Pax = 0, DistanceNm = 300, RewardCents = jobReward, HubRepBonusCents = hubBonus, Xp = 20,
            RequiredRank = jobRank, ClientKey = "c:test", ClientName = "Test Client",
            GeneratedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddHours(6),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, 500_000m, "start");
        return (company.Id, inst.Id, staff.Id, job.Id);
    }

    [Fact]
    public async Task Dispatch_OnReconcile_MovesAircraftAndCrew_AndBanksIncome()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);

        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        // en route until the duty-scaled flight time elapses (300nm/150kt = 2h × 24/8 = 6h of wall-clock)
        clock.UtcNow = clock.UtcNow.AddHours(8);
        ReconcileDigest digest;
        using (var db = tdb.NewContext()) digest = await Ops(db, clock).ReconcileAsync(companyId);

        Assert.True(digest.Trips >= 1);
        using (var db = tdb.NewContext())
        {
            var ac = await db.AircraftInstances.FindAsync(aircraftId);
            Assert.Equal("EHRD", ac!.LocationIcao);                         // aircraft flew one-way to the destination
            Assert.Equal(AircraftAvailability.Available, ac.Availability);   // and is free again
            Assert.True(ac.AirframeHours > 0);                              // it logged the hours
            Assert.Equal("EHRD", (await db.Staff.FindAsync(staffId))!.CurrentIcao); // the crew is there too
            Assert.Equal(DispatchStatus.Flown, (await db.DispatchLegs.FirstAsync(d => d.StaffId == staffId)).Status);
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.JobPayout && e.DedupeKey != null && e.DedupeKey.StartsWith("dispatch:"));
            var company = await db.Companies.FindAsync(companyId);
            var ledgerSum = await db.LedgerEntries.SumAsync(e => e.AmountCents);
            Assert.Equal(ledgerSum, company!.CashCents); // cash reconciles to the ledger
        }
    }

    [Fact]
    public async Task Dispatch_OnReconcile_UpdatesTheClient_ButLessThanFlyingYourself()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);

        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        clock.UtcNow = clock.UtcNow.AddHours(8);
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId);

        using (var db = tdb.NewContext())
        {
            var client = await db.Clients.SingleOrDefaultAsync(c => c.ClientKey == "c:test");
            Assert.NotNull(client);                                                 // the client heard about the crew delivery
            Assert.Equal(EconomyConfig.Default.CrewLegClientLoyaltyMilli, client!.LoyaltyMilli); // a smaller lift than a player leg
            Assert.True(client.LoyaltyMilli < EconomyConfig.Default.ClientLoyaltyFullMilli);      // less impressed than if you flew
        }
    }

    [Fact]
    public async Task Dispatch_RemovesTheJobFromTheBoard()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        using (var db = tdb.NewContext()) Assert.Null(await db.Jobs.FindAsync(jobId)); // committed to the crew — off the board
    }

    [Fact]
    public async Task Dispatch_NotYetReady_DoesNotSettle()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        // reconcile with (almost) no time elapsed — the leg is still en route
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId);
        using (var db = tdb.NewContext())
        {
            Assert.Equal(DispatchStatus.Flying, (await db.DispatchLegs.FirstAsync(d => d.StaffId == staffId)).Status);
            Assert.Equal("EHAM", (await db.AircraftInstances.FindAsync(aircraftId))!.LocationIcao); // hasn't moved
            Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => e.DedupeKey != null && e.DedupeKey.StartsWith("dispatch:"));
        }
    }

    [Fact]
    public async Task Dispatch_IsIdempotent_BanksOnce()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        clock.UtcNow = clock.UtcNow.AddHours(8);
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId);
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId); // replay
        using (var db = tdb.NewContext())
        {
            var legId = (await db.DispatchLegs.FirstAsync(d => d.StaffId == staffId)).Id;
            Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.JobPayout && e.DedupeKey == $"dispatch:{legId}"));
        }
    }

    [Fact]
    public async Task Dispatch_Rejects_WrongLocation_NotRated_AndAlreadyFlying()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);

        // aircraft moved away from the job origin → rejected
        using (var db = tdb.NewContext())
        {
            var ac = await db.AircraftInstances.FindAsync(aircraftId); ac!.LocationIcao = "EHRD"; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId));
            ac.LocationIcao = "EHAM"; await db.SaveChangesAsync();
        }
        // dispatch it, then a SECOND dispatch of the same crew is blocked (already flying)
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        using (var db = tdb.NewContext())
        {
            var job2 = new Job { Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Mail", WeightLbs = 400, DistanceNm = 300, RewardCents = 100_000, RequiredRank = PilotRank.Trainee, GeneratedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddHours(6) };
            db.Jobs.Add(job2); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, job2.Id, staffId, aircraftId));
        }
    }

    private static Job Jb(string origin, string dest, FakeClock c)
        => new() { Id = Guid.NewGuid(), Type = MissionType.Cargo, OriginIcao = origin, DestIcao = dest, Commodity = "Mail", WeightLbs = 200, DistanceNm = 300, RewardCents = 100_000, RequiredRank = PilotRank.Trainee, GeneratedAt = c.UtcNow, ExpiresAt = c.UtcNow.AddHours(6) };

    [Fact]
    public async Task Dispatch_ChainsUpToThreeConnectedLegs_ThenFliesTheWholeItinerary()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock); // leg 1: EHAM → EHRD
        Job j2, j3, j4;
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(A("EHEH", EhehLat, EhehLon), A("EHGG", 53.12, 6.58), A("EHTW", 52.27, 6.87));
            db.Jobs.AddRange(j2 = Jb("EHRD", "EHEH", clock), j3 = Jb("EHEH", "EHGG", clock), j4 = Jb("EHGG", "EHTW", clock));
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId); // leg 1
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, j2.Id, staffId, aircraftId); // append leg 2
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, j3.Id, staffId, aircraftId); // append leg 3
        using (var db = tdb.NewContext()) // a 4th leg exceeds the cap
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, j4.Id, staffId, aircraftId));

        clock.UtcNow = clock.UtcNow.AddHours(20); // all three legs' duty-scaled times elapse (6h + 6h + 6h)
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId);
        using (var db = tdb.NewContext())
        {
            var ac = await db.AircraftInstances.FindAsync(aircraftId);
            Assert.Equal("EHGG", ac!.LocationIcao);                       // ended at the last leg's destination
            Assert.Equal(AircraftAvailability.Available, ac.Availability); // whole itinerary flown → freed
            Assert.Equal("EHGG", (await db.Staff.FindAsync(staffId))!.CurrentIcao);
            Assert.Equal(3, await db.DispatchLegs.CountAsync(d => d.StaffId == staffId && d.Status == DispatchStatus.Flown));
            Assert.Equal(3, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.JobPayout && e.DedupeKey != null && e.DedupeKey.StartsWith("dispatch:")));
        }
    }

    [Fact]
    public async Task Dispatch_Append_RejectsADisconnectedOrigin()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock); // leg 1 ends at EHRD
        Job bad;
        using (var db = tdb.NewContext())
        {
            db.Airports.Add(A("EHEH", EhehLat, EhehLon));
            db.Jobs.Add(bad = Jb("EHAM", "EHEH", clock)); // departs EHAM, but the run is at EHRD after leg 1
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, bad.Id, staffId, aircraftId));
    }

    [Fact]
    public async Task RecallDispatch_CancelsTheLegAndEverythingDownstream()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock); // leg 1 EHAM→EHRD
        Job j2, j3;
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(A("EHEH", EhehLat, EhehLon), A("EHGG", 53.12, 6.58));
            db.Jobs.AddRange(j2 = Jb("EHRD", "EHEH", clock), j3 = Jb("EHEH", "EHGG", clock));
            await db.SaveChangesAsync();
        }
        Guid leg2Id;
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        using (var db = tdb.NewContext()) leg2Id = (await Ops(db, clock).DispatchJobAsync(companyId, j2.Id, staffId, aircraftId)).Id;
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, j3.Id, staffId, aircraftId);
        // recall the SECOND leg → legs 2 and 3 go, leg 1 stays
        using (var db = tdb.NewContext()) await Ops(db, clock).CancelDispatchAsync(companyId, leg2Id);
        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.DispatchLegs.CountAsync(d => d.StaffId == staffId && d.Status == DispatchStatus.Flying && !d.IsDeleted)); // only leg 1
            Assert.Equal(AircraftAvailability.Reserved, (await db.AircraftInstances.FindAsync(aircraftId))!.Availability); // leg 1 still holds it
        }
    }

    [Fact]
    public async Task Dispatch_RejectsAJobAboveThePilotsRank()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        // the job needs a Captain; the player is a Trainee — accept refuses it, so dispatch must too (no bypass)
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock, jobRank: PilotRank.Captain, pilotRank: PilotRank.Trainee);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId));
    }

    [Fact]
    public async Task Dispatch_RejectsAReputationGatedJob_WhenThePilotIsBelowThreshold()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        // an Emergency run is reputation-gated; a low-rep player can't accept it, so a crew can't be dispatched on it (L12)
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock, jobType: MissionType.Emergency, pilotRepMilli: 0);
        using var db = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId));
    }

    [Fact]
    public async Task Dispatch_RejectsALeasedAircraft()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock, ownership: OwnershipKind.Rented);
        using (var db = tdb.NewContext())
        {
            db.RentalAgreements.Add(new RentalAgreement
            {
                Id = Guid.NewGuid(), CompanyId = companyId, AircraftInstanceId = aircraftId, Kind = RentalKind.Lease, Status = RentalStatus.Active,
                DepositCents = 50_000, HoldingPerDayCents = 0, FlightHourRateCents = 0, LastRentBilledAt = clock.UtcNow, HoursLastBilled = 0,
                StartedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(30), UpdatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        using var db2 = tdb.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db2, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId)); // a lease is hand-fly-only
    }

    [Fact]
    public async Task Dispatch_MakesCrewFlying_BlockingDismissAndReposition()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        using (var db = tdb.NewContext())
        {
            Assert.True(await Ops(db, clock).CrewAlreadyFlyingAsync(staffId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).DismissAsync(companyId, staffId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ops(db, clock).RelocateCrewAsync(companyId, staffId, "EHRD"));
        }
    }

    [Fact]
    public async Task CancelDispatch_FreesTheAircraftAtOrigin_AndForfeitsTheJob()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock);
        Guid legId;
        using (var db = tdb.NewContext()) legId = (await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId)).Id;
        using (var db = tdb.NewContext()) await Ops(db, clock).CancelDispatchAsync(companyId, legId);
        using (var db = tdb.NewContext())
        {
            var ac = await db.AircraftInstances.FindAsync(aircraftId);
            Assert.Equal(AircraftAvailability.Available, ac!.Availability); // freed
            Assert.Equal("EHAM", ac.LocationIcao);                          // never departed → still at origin
            Assert.True((await db.DispatchLegs.FindAsync(legId))!.IsDeleted);
            Assert.False(await Ops(db, clock).CrewAlreadyFlyingAsync(staffId)); // crew free again
        }
    }

    [Fact]
    public async Task Dispatch_InARental_IsAllowed_AccruesHours_AndStripsTheHubLift()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        // a rented tail + a job whose reward carries a $50 hub-reputation bonus that a rental must NOT earn
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock, jobReward: 200_000, hubBonus: 5_000, ownership: OwnershipKind.Rented);
        using (var db = tdb.NewContext())
        {
            // a real rental agreement so the reconcile usage-fee loop bills the flown hours
            db.RentalAgreements.Add(new RentalAgreement
            {
                Id = Guid.NewGuid(), CompanyId = companyId, AircraftInstanceId = aircraftId, Kind = RentalKind.Rental, Status = RentalStatus.Active,
                HoursAtPickup = 0, HullMilliAtPickup = 100_000, EngineMilliAtPickup = 100_000,
                DepositCents = 50_000, HoldingPerDayCents = 200, FlightHourRateCents = 4_000,
                LastRentBilledAt = clock.UtcNow, HoursLastBilled = 0, StartedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.AddDays(7), UpdatedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        DispatchLeg leg;
        using (var db = tdb.NewContext()) leg = await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        Assert.Equal(195_000, leg.RewardCents); // 200000 − 5000 hub bonus (a rental earns no hub premium)

        clock.UtcNow = clock.UtcNow.AddHours(8);
        using (var db = tdb.NewContext()) await Ops(db, clock).ReconcileAsync(companyId);
        using (var db = tdb.NewContext())
        {
            Assert.True((await db.AircraftInstances.FindAsync(aircraftId))!.AirframeHours > 0); // hours logged on the rental
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.AircraftRental && e.AmountCents < 0); // usage fee billed
        }
    }

    [Fact]
    public async Task Dispatch_ByASharpCrew_LiftsOperatingReputation_MoneyNeutral()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        var (companyId, aircraftId, staffId, jobId) = await SeedAsync(tdb, clock); // crew skill 90% > starting rep 0
        using (var db = tdb.NewContext()) await Ops(db, clock).DispatchJobAsync(companyId, jobId, staffId, aircraftId);
        clock.UtcNow = clock.UtcNow.AddHours(8);
        ReconcileDigest digest;
        using (var db = tdb.NewContext()) digest = await Ops(db, clock).ReconcileAsync(companyId);
        Assert.True(digest.OperatingRepDeltaMilli > 0); // eased toward the sharp crew's competence
        using (var db = tdb.NewContext())
        {
            Assert.True((await db.Companies.FindAsync(companyId))!.OperatingReputationMilli > 0);
            Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => e.Description.Contains("reputation")); // never a ledger row
        }
    }
}
