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
    public async Task RentedTail_CannotBeServiced_TheLessorMaintainsIt()
    {
        // The load-bearing guard: without it a renter could wreck a tail, service it to 100% for a flat fee,
        // and reclaim the whole damage deposit — nulling the mechanic. Maintain/Inspect/Ferry are Owned-only.
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        using var db = tdb.NewContext();
        var tailId = (await db.RentalAgreements.FindAsync(agId))!.AircraftInstanceId;
        var dealer = Dealer(db, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dealer.MaintainAsync(s.CompanyId, tailId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dealer.InspectAsync(s.CompanyId, tailId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dealer.RelocateAsync(s.CompanyId, tailId, "EHRD"));
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
    public async Task OrdinaryFirmLeg_WithinTheHandlingBand_DamageZero()
    {
        // 10 h flown, condition ~10000 milli below the hours line — a firm-but-acceptable landing / minor wear,
        // inside the Fun-Dial handling allowance (10 h × 1200 = 12000). It must NOT touch the deposit (L9).
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        int cond = Cfg.RentalDeliveryConditionMilli - (int)Math.Round(10.0 * Cfg.ConditionWearMilliPerHour) - 10_000;
        await FlyRental(tdb, agId, 10, cond, cond);

        using var db = tdb.NewContext();
        var q = await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, agId);
        Assert.Equal(0, q!.DamageCents);
        Assert.Equal(s.DepositCents, q.RefundCents);
    }

    [Fact]
    public async Task HardLeg_ChargesDamageBeyondTheHandlingBand_CappedAtTheDeposit()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await RentAsync(tdb, clock, s);
        // 10 h flown, condition well past the ordinary-handling band — genuine abuse (a real slam / engine damage).
        await FlyRental(tdb, agId, 10, Cfg.RentalDeliveryConditionMilli - 40_000, Cfg.RentalDeliveryConditionMilli - 40_000);

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

    // ── Phase 9f-2: lease + rent-to-own ───────────────────────────────────────────────────────────

    private static async Task<Guid> LeaseAsync(TestDb tdb, IClock clock, Seed s, int termDays = 28)
    {
        using var db = tdb.NewContext();
        var tail = await Dealer(db, clock).LeaseAsync(s.CompanyId, s.TypeId, "EHAM", termDays);
        return (await db.RentalAgreements.SingleAsync(a => a.AircraftInstanceId == tail.Id)).Id;
    }

    private static async Task FlyLease(TestDb tdb, Guid agreementId, double addHours, int hullMilli, int engineMilli)
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
    public async Task Lease_ChargesDepositPlusUpfrontWeeks_AndOpensALeaseAgreement()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        long deposit = Cfg.LeaseDepositCents(s.StickerCents);
        long upfrontRent = Cfg.LeaseWeeklyRateCents(s.StickerCents, 28) * Cfg.LeaseUpfrontWeeks;
        var agId = await LeaseAsync(tdb, clock, s);

        using var db = tdb.NewContext();
        var ag = await db.RentalAgreements.FindAsync(agId);
        Assert.Equal(RentalKind.Lease, ag!.Kind);
        Assert.Equal(RentalStatus.Active, ag.Status);
        Assert.Equal(upfrontRent, ag.RentCreditedCents);  // the up-front weeks pre-credit the buyout
        var company = await db.Companies.FindAsync(s.CompanyId);
        Assert.Equal(StartCash - deposit - upfrontRent, company!.CashCents); // deposit escrow + up-front weeks both leave cash
        Assert.Equal(company.CashCents, await LedgerSum(db, s.CompanyId));
        var tail = await db.AircraftInstances.FindAsync(ag.AircraftInstanceId);
        Assert.Equal(OwnershipKind.Rented, tail!.Ownership);
    }

    [Fact]
    public async Task Lease_ReconcileBillsWeeklyRentPlusHullCover_Idempotently()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await LeaseAsync(tdb, clock, s);
        clock.UtcNow = clock.UtcNow.AddDays(35); // past the 21-day pre-billed window → ~14 billable days

        long afterFirst;
        using (var db = tdb.NewContext())
        {
            var d = await Ops(db, clock).ReconcileAsync(s.CompanyId);
            Assert.True(d.RentalCents > 0);
            afterFirst = (await db.Companies.FindAsync(s.CompanyId))!.CashCents;
        }
        using (var db = tdb.NewContext())
        {
            await Ops(db, clock).ReconcileAsync(s.CompanyId); // same clock → whole billed days already taken
            Assert.Equal(afterFirst, (await db.Companies.FindAsync(s.CompanyId))!.CashCents);
        }
    }

    [Fact]
    public async Task LeaseReturn_ChargesTheConsumedLifeDeficit_CappedAtTheDeposit()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        long deposit = Cfg.LeaseDepositCents(s.StickerCents);

        // Returned at pickup condition → no shortfall → full deposit back.
        var clean = await LeaseAsync(tdb, clock, s);
        using (var db = tdb.NewContext())
            Assert.Equal(deposit, (await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, clean))!.RefundCents);

        // Returned 10 points below pickup → a partial deficit charge (under the deposit cap), refund > 0.
        var worn = await LeaseAsync(tdb, clock, s);
        await FlyLease(tdb, worn, 20, 90_000, 90_000);
        using (var db = tdb.NewContext())
        {
            var q = await Dealer(db, clock).ReturnQuoteAsync(s.CompanyId, worn);
            Assert.True(q!.DamageCents > 0);
            Assert.True(q.DamageCents < deposit);        // deficit value 0.70x0.10 = 7% of market < the 8% deposit → uncapped
            Assert.Equal(deposit - q.DamageCents, q.RefundCents);
            Assert.True(q.RefundCents > 0);
        }
    }

    [Fact]
    public async Task Buyout_PricesAboveResale_SoBuyoutThenSell_AlwaysLoses()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await LeaseAsync(tdb, clock, s);

        long buyout, resaleAtBuyout, proceeds;
        using (var db = tdb.NewContext())
        {
            var dealer = Dealer(db, clock);
            var tail = await db.AircraftInstances.FirstAsync(a => a.CompanyId == s.CompanyId);
            var type = await db.AircraftTypes.FirstAsync();
            resaleAtBuyout = dealer.ResaleValueCents(tail, type);
            buyout = await dealer.BuyoutAsync(s.CompanyId, agId);
            Assert.True(buyout > resaleAtBuyout, $"buyout {buyout} must exceed resale {resaleAtBuyout} (the landmine guard)");
        }
        using (var db = tdb.NewContext())
        {
            var tail = await db.AircraftInstances.FirstAsync(a => a.CompanyId == s.CompanyId && !a.IsDeleted);
            Assert.Equal(OwnershipKind.Owned, tail.Ownership);      // it's yours now
            Assert.Equal(buyout, tail.PurchasePriceCents);
            Assert.Equal(RentalStatus.PurchasedOut, (await db.RentalAgreements.FindAsync(agId))!.Status);
            proceeds = await Dealer(db, clock).SellAsync(s.CompanyId, tail.Id); // sell it straight back
        }
        Assert.True(proceeds < buyout, $"selling ({proceeds}) after a buyout ({buyout}) must always lose");
    }

    [Fact]
    public async Task LeaseBuyoutFloor_StaysAboveTheResaleFactor()
        => Assert.True(Cfg.UsedPriceFactor(Cfg.LeaseBuyoutConditionFloorMilli) > Cfg.AircraftResaleFactor,
            "the buyout is priced through UsedPriceFactor(floor); it must stay above AircraftResaleFactor or a buyout-then-flip could profit");

    [Fact]
    public async Task Casualty_OnAWriteOff_PaysTheDeductible_RefundsTheDeposit_RetiresTheTail()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        long deposit = Cfg.LeaseDepositCents(s.StickerCents);
        long expectedDeductible = Cfg.LeaseCasualtyDeductibleCents(s.StickerCents);

        // Not a write-off yet → refused.
        var notLoss = await LeaseAsync(tdb, clock, s);
        using (var db = tdb.NewContext())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dealer(db, clock).CasualtyAsync(s.CompanyId, notLoss));

        // Flown to a total loss (≤ 25%) → pays the deductible only, deposit refunds, tail written off.
        await FlyLease(tdb, notLoss, 30, 15_000, 15_000);
        long cashBefore;
        using (var db = tdb.NewContext()) cashBefore = (await db.Companies.FindAsync(s.CompanyId))!.CashCents;
        long deductible;
        using (var db = tdb.NewContext()) deductible = await Dealer(db, clock).CasualtyAsync(s.CompanyId, notLoss);

        Assert.Equal(expectedDeductible, deductible);
        using var db2 = tdb.NewContext();
        var ag = await db2.RentalAgreements.FindAsync(notLoss);
        Assert.Equal(RentalStatus.WrittenOff, ag!.Status);
        Assert.True((await db2.AircraftInstances.FindAsync(ag.AircraftInstanceId))!.IsDeleted);
        // Net cash change = deposit refund − deductible (no rent accrued at t0).
        Assert.Equal(cashBefore + deposit - deductible, (await db2.Companies.FindAsync(s.CompanyId))!.CashCents);
        Assert.Equal((await db2.Companies.FindAsync(s.CompanyId))!.CashCents, await LedgerSum(db2, s.CompanyId));
    }

    [Fact]
    public async Task LeasedTail_IsHandFlyOnly_AndNotAnAsset()
    {
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        await LeaseAsync(tdb, clock, s);
        using var db = tdb.NewContext();
        var tailId = (await db.AircraftInstances.FirstAsync(a => a.CompanyId == s.CompanyId)).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dealer(db, clock).SellAsync(s.CompanyId, tailId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dealer(db, clock).MaintainAsync(s.CompanyId, tailId));
        var ins = new InsuranceService(db, new LedgerService(db, clock), clock, Cfg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ins.InsureAsync(s.CompanyId, tailId, null));
    }

    [Fact]
    public async Task RentedTail_FlownPast100Hours_IsNotGroundedOnInspection_ButStillGroundsOnCondition()
    {
        // The 9f-2 review's HIGH: a non-owned tail flown past the 100-hour interval must NOT ground (the holder
        // can't clear an inspection — the lessor keeps it up), but a wrecked one still grounds on the condition floor.
        using var tdb = new TestDb(); var clock = new FakeClock();
        var s = await SeedAsync(tdb, clock, C172());
        var agId = await LeaseAsync(tdb, clock, s);
        await FlyLease(tdb, agId, 150, 90_000, 90_000); // well past the 100-h interval, still good condition

        using var db = tdb.NewContext();
        var dealer = Dealer(db, clock);
        var tail = await db.AircraftInstances.FindAsync((await db.RentalAgreements.FindAsync(agId))!.AircraftInstanceId);
        Assert.Equal(OwnershipKind.Rented, tail!.Ownership);
        Assert.True(dealer.Airworthiness(tail).Airworthy);    // never grounded on the 100-h interval — the lessor maintains it

        tail.HullConditionMilli = tail.EngineConditionMilli = 10_000; // wrecked, below the airworthy floor
        Assert.False(dealer.Airworthiness(tail).Airworthy);   // the condition floor still bites → return or casualty
    }
}
