using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Phase 9f-1 — aircraft rental. The money-critical invariants: the escrow nets to zero on a clean return,
/// only real damage BEYOND ordinary hours-wear bills (capped at the deposit), a rented tail can be neither
/// sold, insured, nor put on autonomous work, and net worth never books phantom equity.
/// </summary>
public class RentalServiceTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private const long StartCash = 100_000_000; // $1,000,000 — plenty for any deposit

    private static AircraftType C172() => new()
    {
        Id = Guid.NewGuid(), Key = "C172", CanonicalName = "Cessna 172 Skyhawk",
        Category = AircraftCategory.LightSingle, Seats = 4, UsefulLoadLbs = 900, CruiseKtas = 120,
    };

    private sealed record Seed(Guid CompanyId, Guid TypeId, long StickerCents, long DepositCents);

    private static async Task<Seed> SeedAsync(TestDb tdb, IClock clock, AircraftType type)
    {
        using var db = tdb.NewContext();
        db.Companies.Add(new Company { Id = Guid.NewGuid(), Name = "Co" });
        db.AircraftTypes.Add(type);
        await db.SaveChangesAsync();
        var companyId = (await db.Companies.FirstAsync()).Id;
        await new LedgerService(db, clock).PostAsync(companyId, LedgerCategory.StartingBalance, StartCash / 100m, "start");
        long sticker = AircraftPricing.Quote(Cfg, type).TotalCents;
        return new Seed(companyId, type.Id, sticker, Cfg.RentDepositCents(sticker));
    }

    private static AircraftDealerService Dealer(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);
    private static OperationsService Ops(CallsignDbContext db, IClock clock) => new(db, new LedgerService(db, clock), clock, Cfg);
    private static Task<long> LedgerSum(CallsignDbContext db, Guid companyId)
        => db.LedgerEntries.Where(e => e.AccountId == companyId).SumAsync(e => e.AmountCents);

    private static async Task<Guid> RentAsync(TestDb tdb, IClock clock, Seed s)
    {
        using var db = tdb.NewContext();
        var tail = await Dealer(db, clock).RentAsync(s.CompanyId, s.TypeId, "EHAM");
        return (await db.RentalAgreements.SingleAsync(a => a.AircraftInstanceId == tail.Id)).Id;
    }

    // Simulate a hand-flown leg on a rented tail: add hours + set the returned condition.
    private static async Task FlyRental(TestDb tdb, Guid agreementId, double addHours, int hullMilli, int engineMilli)
    {
        using var db = tdb.NewContext();
        var ag = await db.RentalAgreements.FindAsync(agreementId);
        var tail = await db.AircraftInstances.FindAsync(ag!.AircraftInstanceId);
        tail!.AirframeHours += addHours;
        tail.HullConditionMilli = hullMilli;
        tail.EngineConditionMilli = engineMilli;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Rent_EscrowsTheDeposit_DeliversARentedTail_AndOpensAnAgreement()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);

        using var db = tdb.NewContext();
        var ag = await db.RentalAgreements.FindAsync(agId);
        var tail = await db.AircraftInstances.FindAsync(ag!.AircraftInstanceId);
        Assert.Equal(OwnershipKind.Rented, tail!.Ownership);
        Assert.Null(tail.PurchasePriceCents);                 // you did not buy it
        Assert.Equal(RentalStatus.Active, ag.Status);
        Assert.Equal(s.DepositCents, ag.DepositCents);
        var company = await db.Companies.FindAsync(s.CompanyId);
        Assert.Equal(StartCash - s.DepositCents, company!.CashCents); // the escrow actually leaves cash
        Assert.Equal(company.CashCents, await LedgerSum(db, s.CompanyId)); // ledger-exact
    }

    [Fact]
    public async Task CleanReturn_RefundsTheWholeDeposit_EscrowNetsToZero()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);       // no flying, no time passes → holding 0, usage 0, damage 0

        long refund;
        using (var db = tdb.NewContext())
            refund = await Dealer(db, clock).ReturnAsync(s.CompanyId, agId);

        Assert.Equal(s.DepositCents, refund);
        using var db2 = tdb.NewContext();
        var company = await db2.Companies.FindAsync(s.CompanyId);
        Assert.Equal(StartCash, company!.CashCents);              // rent → return nets to zero for a clean flight
        Assert.Equal(company.CashCents, await LedgerSum(db2, s.CompanyId));
        var ag = await db2.RentalAgreements.FindAsync(agId);
        Assert.Equal(RentalStatus.Returned, ag!.Status);
        var tail = await db2.AircraftInstances.FindAsync(ag.AircraftInstanceId);
        Assert.True(tail!.IsDeleted);                             // goes back to the owner, off your books
    }

    [Fact]
    public async Task RentedTail_CannotBeSold()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        using var db = tdb.NewContext();
        var tailId = (await db.RentalAgreements.FindAsync(agId))!.AircraftInstanceId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dealer(db, clock).SellAsync(s.CompanyId, tailId));
    }

    [Fact]
    public async Task RentedTail_CannotBeInsured_NoInsurableInterest()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        using var db = tdb.NewContext();
        var tailId = (await db.RentalAgreements.FindAsync(agId))!.AircraftInstanceId;
        var ins = new InsuranceService(db, new LedgerService(db, clock), clock, Cfg);
        Assert.Null(await ins.QuoteAsync(s.CompanyId, tailId, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ins.InsureAsync(s.CompanyId, tailId, null));
    }

    [Fact]
    public async Task RentedTail_CannotFlyAutonomousWork_HandFlyOnly()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        Guid staffId, tailId;
        using (var db = tdb.NewContext())
        {
            var staff = new Staff
            {
                Id = Guid.NewGuid(), CompanyId = s.CompanyId, Name = "Amelia", Role = StaffRole.Pilot,
                WagePerDayCents = 20_000, SkillMilli = 60_000, HiredAt = clock.UtcNow, LastPaidAt = clock.UtcNow, IsActive = true, UpdatedAt = clock.UtcNow,
            };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            staffId = staff.Id;
            tailId = (await db.RentalAgreements.FindAsync(agId))!.AircraftInstanceId;
        }
        using (var db = tdb.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Ops(db, clock).CreateStandingOrderAsync(s.CompanyId, staffId, tailId, "EHRD"));
            Assert.Contains("hand-fly-only", ex.Message);
        }
    }

    [Fact]
    public async Task NetWorth_ExcludesTheRentedTail_ButCountsTheDepositAsAReceivable()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());

        long nwBefore;
        using (var db = tdb.NewContext())
            nwBefore = (await new FinanceService(db, clock, Cfg).NetWorthAsync(s.CompanyId)).NetWorthCents;
        Assert.Equal(StartCash, nwBefore);

        await RentAsync(tdb, clock, s);
        using (var db = tdb.NewContext())
        {
            var nw = await new FinanceService(db, clock, Cfg).NetWorthAsync(s.CompanyId);
            Assert.Equal(StartCash, nw.NetWorthCents); // cash −deposit, receivable +deposit → net worth is FLAT (no phantom equity, no hit)
        }
    }

    [Fact]
    public async Task CoachBandLeg_LeavesConditionAtTheExpectedWearLine_DamageZero_FullRefund()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        // 10 h flown, condition down by EXACTLY the ordinary hourly wear (10 × 400 = 4000 milli) — a clean leg.
        int expected = Cfg.RentalDeliveryConditionMilli - (int)Math.Round(10.0 * Cfg.ConditionWearMilliPerHour);
        await FlyRental(tdb, agId, 10, expected, expected);

        using var db = tdb.NewContext();
        var q = await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, agId);
        Assert.Equal(0, q!.DamageCents);                 // ordinary wear is paid for by usage rent — free
        Assert.Equal(s.DepositCents, q.RefundCents);
    }

    [Fact]
    public async Task HardLeg_ChargesDamageBeyondOrdinaryWear_CappedAtTheDeposit()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        // 10 h flown, but condition 10% below the expected line — real abuse.
        int expected = Cfg.RentalDeliveryConditionMilli - (int)Math.Round(10.0 * Cfg.ConditionWearMilliPerHour);
        await FlyRental(tdb, agId, 10, expected - 10_000, expected - 10_000);

        long refund;
        using (var db = tdb.NewContext())
        {
            var q = await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, agId);
            Assert.True(q!.DamageCents > 0);
            Assert.True(q.DamageCents <= s.DepositCents);        // never more than the escrow
            Assert.Equal(s.DepositCents - q.DamageCents, q.RefundCents);
            refund = q.RefundCents;
        }
        using (var db = tdb.NewContext())
        {
            await Dealer(db, clock).ReturnAsync(s.CompanyId, agId);
            var company = await db.Companies.FindAsync(s.CompanyId);
            long usage = 10 * Cfg.RentFlightHourCents(s.StickerCents); // 10 h of usage rent is also billed at return
            long damage = s.DepositCents - refund;
            Assert.Equal(StartCash - usage - damage, company!.CashCents); // cash falls by exactly the usage + the damage
            Assert.Equal(company.CashCents, await LedgerSum(db, s.CompanyId));
        }
    }

    [Fact]
    public async Task WreckedLeg_DamageIsCappedAtTheDeposit_RefundZero_NeverNegative()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        await FlyRental(tdb, agId, 10, 3_000, 3_000); // near-total loss

        using var db = tdb.NewContext();
        var q = await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, agId);
        Assert.Equal(s.DepositCents, q!.DamageCents);    // clamped to the deposit — liability can't exceed the escrow
        Assert.Equal(0, q.RefundCents);                  // never negative
    }

    [Fact]
    public async Task Return_IsIdempotent_NoDoubleRefund()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);

        long first, second;
        using (var db = tdb.NewContext()) first = await Dealer(db, clock).ReturnAsync(s.CompanyId, agId);
        using (var db = tdb.NewContext()) second = await Dealer(db, clock).ReturnAsync(s.CompanyId, agId);
        Assert.Equal(first, second);                     // replayed the same refund

        using var db2 = tdb.NewContext();
        Assert.Equal(StartCash, (await db2.Companies.FindAsync(s.CompanyId))!.CashCents); // not credited twice
        Assert.Equal(1, await db2.LedgerEntries.CountAsync(e => e.DedupeKey == $"rental-return:{agId}"));
    }

    [Fact]
    public async Task Reconcile_BillsHoldingAndUsage_AndIsIdempotent()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        clock.UtcNow = clock.UtcNow.AddDays(2);
        int expected = Cfg.RentalDeliveryConditionMilli - (int)Math.Round(10.0 * Cfg.ConditionWearMilliPerHour);
        await FlyRental(tdb, agId, 10, expected, expected);

        long afterFirst;
        using (var db = tdb.NewContext())
        {
            var d = await Ops(db, clock).ReconcileAsync(s.CompanyId);
            Assert.True(d.RentalCents > 0);              // holding (2 days) + usage (10 h) both billed
            afterFirst = (await db.Companies.FindAsync(s.CompanyId))!.CashCents;
        }
        using (var db = tdb.NewContext())
        {
            await Ops(db, clock).ReconcileAsync(s.CompanyId); // same clock → nothing more to bill
            Assert.Equal(afterFirst, (await db.Companies.FindAsync(s.CompanyId))!.CashCents);
        }
    }

    [Fact]
    public async Task Reconcile_AutoReturnsAnExpiredIdleRental_ButNotOneMidLeg()
    {
        // Idle expired rental → auto-returned and off the books.
        using (var tdb = new TestDb())
        {
            var clock = new FakeClock();
            var s = await SeedAsync(tdb, clock, C172());
            var agId = await RentAsync(tdb, clock, s);
            clock.UtcNow = clock.UtcNow.AddDays(Cfg.RentTermDefaultDays + 1);
            using (var db = tdb.NewContext())
            {
                var d = await Ops(db, clock).ReconcileAsync(s.CompanyId);
                Assert.Single(d.RentalsAutoReturned!);
            }
            using var db2 = tdb.NewContext();
            Assert.Equal(RentalStatus.Returned, (await db2.RentalAgreements.FindAsync(agId))!.Status);
        }

        // A rental still out on a leg (not Available) keeps accruing and is NOT auto-returned.
        using (var tdb = new TestDb())
        {
            var clock = new FakeClock();
            var s = await SeedAsync(tdb, clock, C172());
            var agId = await RentAsync(tdb, clock, s);
            using (var db = tdb.NewContext())
            {
                var ag = await db.RentalAgreements.FindAsync(agId);
                var tail = await db.AircraftInstances.FindAsync(ag!.AircraftInstanceId);
                tail!.Availability = AircraftAvailability.InFlight;
                await db.SaveChangesAsync();
            }
            clock.UtcNow = clock.UtcNow.AddDays(Cfg.RentTermDefaultDays + 1);
            using (var db = tdb.NewContext())
            {
                var d = await Ops(db, clock).ReconcileAsync(s.CompanyId);
                Assert.Empty(d.RentalsAutoReturned!);
            }
            using var db2 = tdb.NewContext();
            Assert.Equal(RentalStatus.Active, (await db2.RentalAgreements.FindAsync(agId))!.Status);
        }
    }

    [Theory]
    [InlineData(20_000_000)]   // $200k light single
    [InlineData(500_000_000)]  // $5M turboprop
    [InlineData(4_000_000_000)]// $40M jet
    public void NoMoneyPump_UsageRatePerHour_ExceedsTheOwnershipWearItSubstitutes(long stickerCents)
    {
        long usagePerHour = Cfg.RentFlightHourCents(stickerCents);
        long ownershipWearValuePerHour = (long)Math.Round(stickerCents * Cfg.AircraftResaleFactor * (Cfg.ConditionWearMilliPerHour / 100_000.0));
        Assert.True(usagePerHour > ownershipWearValuePerHour,
            $"renting a flight-hour ({usagePerHour}) must cost more than the resale value ordinary wear consumes ({ownershipWearValuePerHour}) — else rent-to-earn pumps");
    }
}
