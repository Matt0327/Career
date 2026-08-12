using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// Every tunable number in the economy, in one versioned place (domain-notes: retunes ship as
/// versions without breaking captured quotes). Designed from first principles — not copied from any
/// existing product — and freely retunable. All money is integer cents.
/// </summary>
public sealed record EconomyConfig
{
    public int Version { get; init; } = 1;

    // --- Cargo reward = base + distance-rate + weight-rate ---
    public long CargoBaseFeeCents { get; init; } = 15_000;   // $150 to show up
    public long CargoPerNmCents { get; init; } = 900;        // $9 per nautical mile
    public long CargoPerLbCents { get; init; } = 60;         // $0.60 per lb of freight

    // --- XP ---
    public int XpBase { get; init; } = 5;
    public double XpPerNm { get; init; } = 0.1;

    // --- Generation bounds ---
    public int MinCargoWeightLbs { get; init; } = 200;
    public int MaxCargoWeightLbs { get; init; } = 3_000;
    public double MinJobDistanceNm { get; init; } = 5;       // short regional hops are welcome (quick legs to fly)
    public double MaxJobDistanceNm { get; init; } = 400;
    public double JobDistanceBiasExponent { get; init; } = 3; // >1 favours nearer airports so short hops reliably appear
    public int JobOfferHours { get; init; } = 6;             // how long an offer stays on the board
    public double ArrivalRadiusNm { get; init; } = 5;        // must land within this of the destination to settle

    public long CargoRewardCents(double distanceNm, int weightLbs)
        => CargoBaseFeeCents
         + (long)Math.Round(distanceNm * CargoPerNmCents)
         + weightLbs * CargoPerLbCents;

    public int JobXp(double distanceNm)
        => XpBase + (int)Math.Round(distanceNm * XpPerNm);

    // --- Passenger reward = base + pax * (per-seat-sold fee + per-passenger-mile fare) ---
    public long PaxBaseFeeCents { get; init; } = 15_000;  // $150 dispatch
    public long PaxPerPaxCents { get; init; } = 12_000;   // $120 per seat sold
    public long PaxPerPaxNmCents { get; init; } = 150;    // $1.50 per passenger per nautical mile
    public int MinPax { get; init; } = 1;
    public int MaxPax { get; init; } = 6;                 // freelance charters stay GA-flyable
    public int PaxWeightLbs { get; init; } = 210;         // body + bag, so useful-load still bites

    public long PaxRewardCents(double distanceNm, int pax)
        => PaxBaseFeeCents + pax * (PaxPerPaxCents + (long)Math.Round(distanceNm * PaxPerPaxNmCents));

    // --- Board mix: relative shares each local source gets of a refresh (largest-remainder split) ---
    public double CargoJobShare { get; init; } = 3;
    public double PassengerJobShare { get; init; } = 2;

    // --- Rank gate (Phase 3b): longer, harder legs demand a higher rank. Jobs above your rank are
    //     shown on the board LOCKED with the reason (never hidden); accept is refused server-side. ---
    public double CopilotDistanceNm { get; init; } = 90;
    public double CaptainDistanceNm { get; init; } = 180;
    public double SeniorCaptainDistanceNm { get; init; } = 300;

    public PilotRank RankForDistance(double distanceNm)
    {
        if (distanceNm >= SeniorCaptainDistanceNm) return PilotRank.SeniorCaptain;
        if (distanceNm >= CaptainDistanceNm) return PilotRank.Captain;
        if (distanceNm >= CopilotDistanceNm) return PilotRank.Copilot;
        return PilotRank.Trainee;
    }

    // --- Check-flights (Phase 3d): pay to fly a scored landing and earn a class rating ---
    public long CheckFlightBaseFeeCents { get; init; } = 200_000;    // $2,000 to book the examiner
    public long CheckFlightPerClassFeeCents { get; init; } = 300_000; // + $3,000 per class step up the ladder

    /// <summary>The fee to attempt a check-flight for a class (harder classes cost more).</summary>
    public long CheckFlightFeeCents(QualClass cls) => CheckFlightBaseFeeCents + (long)cls * CheckFlightPerClassFeeCents;

    /// <summary>Stars (3..5) earned by the touchdown, or 0 = failed (firmer than the pass floor).</summary>
    public int CheckFlightStars(double touchdownFpm)
    {
        double f = Math.Abs(touchdownFpm);
        if (f <= 60) return 5;   // greaser
        if (f <= 120) return 4;  // smooth
        if (f <= 200) return 3;  // a pass
        return 0;                // too firm — failed
    }

    // --- Settlement: landing quality (as a fraction of base reward) and payload bonus ---
    public double PayloadMatchXpBonusPct { get; init; } = 0.5;

    /// <summary>Reward multiplier delta from the touchdown rate: a greaser earns a bonus, a slam a
    /// penalty. Decimal so the caller can round money away-from-zero, the one cents convention.</summary>
    public decimal LandingModifierPct(double touchdownFpm)
    {
        double m = Math.Abs(touchdownFpm);
        if (m <= 100) return 0.10m;   // greaser
        if (m <= 200) return 0.05m;   // good
        if (m <= 400) return 0.00m;   // acceptable
        if (m <= 600) return -0.05m;  // firm
        if (m <= 1000) return -0.15m; // hard
        return -0.30m;                // slammed
    }

    // --- Aircraft pricing (buy) — category base + itemised spec premiums (§6.2) ---
    public long AircraftPricePerUsefulLbCents { get; init; } = 2_000;   // $20 per useful-load lb
    public long AircraftPricePerSeatCents { get; init; } = 150_000;     // $1,500 per seat
    public long AircraftPricePerCruiseKtCents { get; init; } = 300_000; // $3,000 per cruise kt over 100

    /// <summary>Base sticker by category, in cents.</summary>
    public long AircraftBaseCents(AircraftCategory category) => category switch
    {
        AircraftCategory.LightSingle => 40_000_000,     // $400k
        AircraftCategory.LightTwin   => 90_000_000,     // $900k
        AircraftCategory.Turboprop   => 350_000_000,    // $3.5M
        AircraftCategory.LightJet    => 400_000_000,    // $4M
        AircraftCategory.Jet         => 1_500_000_000,  // $15M
        AircraftCategory.Helicopter  => 120_000_000,    // $1.2M
        AircraftCategory.Heavy       => 9_000_000_000,  // $90M
        AircraftCategory.Glider      => 15_000_000,     // $150k
        _                            => 50_000_000,     // $500k
    };

    // --- Running costs: landing fee on arrival + maintenance & condition (§6.6) ---
    /// <summary>The landing/handling fee charged on arrival, by airport size.</summary>
    public long LandingFeeCents(AirportKind kind) => kind switch
    {
        AirportKind.LargeAirport => 25_000,   // $250
        AirportKind.MediumAirport => 8_000,   // $80
        AirportKind.SmallAirport => 2_500,    // $25
        _ => 4_000,                            // $40 (heliport / other)
    };

    public double MaintenanceIntervalHours { get; init; } = 50;    // maintenance is "due" this long after the last
    public long MaintenanceBaseCents { get; init; } = 50_000;      // $500 fixed to open the cowlings
    public long MaintenancePerHourCents { get; init; } = 20_000;   // $200 per airframe hour since the last service
    public int ConditionWearMilliPerHour { get; init; } = 400;     // hull + engine wear per airframe hour (0..100000)
    public int HardLandingWearMilli { get; init; } = 1_500;        // extra hull wear on a hard touchdown

    // --- Bases: one-off setup + recurring rent, by airport size (§4.8) ---
    public long BaseOpenCents(AirportKind kind) => kind switch
    {
        AirportKind.LargeAirport => 5_000_000,   // $50k
        AirportKind.MediumAirport => 2_000_000,  // $20k
        AirportKind.SmallAirport => 500_000,     // $5k
        _ => 1_000_000,                           // $10k
    };
    public long BaseRentPerDayCents(AirportKind kind) => kind switch
    {
        AirportKind.LargeAirport => 20_000,   // $200/day
        AirportKind.MediumAirport => 8_000,   // $80/day
        AirportKind.SmallAirport => 2_000,    // $20/day
        _ => 4_000,                            // $40/day
    };

    // --- Trade (Phase 2g): buy low here, sell high there ---
    /// <summary>How far a good's price swings above/below its catalog base across airports (±fraction).</summary>
    public double TradePriceSwing { get; init; } = 0.35;
    /// <summary>Dealer spread: buy sits this fraction above mid, sell the same below — a same-airport
    /// round trip loses ~2× this, so profit has to come from flying goods somewhere pricier.</summary>
    public decimal TradeSpreadPct { get; init; } = 0.05m;
    /// <summary>How long a market holds its prices before they re-roll (keeps a run plannable).</summary>
    public TimeSpan TradePriceWindow { get; init; } = TimeSpan.FromHours(6);
    /// <summary>Fallback carry capacity when no owned aircraft has a known useful load.</summary>
    public int TradeDefaultHoldLbs { get; init; } = 1_500;

    // --- Loans (Phase 4a) ---
    /// <summary>Repayment horizon for a new loan (straight-line principal over this many days).</summary>
    public int LoanTermDays { get; init; } = 90;

    // --- Balance sheet (Phase 4b) ---
    /// <summary>Resale haircut: a pristine airframe is worth this fraction of its market price as an asset.</summary>
    public double AircraftResaleFactor { get; init; } = 0.70;

    // --- Insurance (Phase 4c) ---
    public int InsuranceDefaultCoverageMilli { get; init; } = 80_000;      // insure 80% of hull by default
    public int InsuranceWeeklyRateBps { get; init; } = 40;                 // 0.40%/week of the covered value
    public int InsuranceDeductibleMilli { get; init; } = 10_000;          // deductible = 10% of covered value
    public int InsuranceTotalLossConditionMilli { get; init; } = 25_000;  // claimable once condition ≤ 25%

    /// <summary>The weekly premium for a covered value.</summary>
    public long InsurancePremiumPerWeekCents(long coveredValueCents)
        => (long)Math.Round(coveredValueCents * (InsuranceWeeklyRateBps / 10_000.0));

    public long InsuranceDeductibleCents(long coveredValueCents)
        => (long)Math.Round(coveredValueCents * (InsuranceDeductibleMilli / 100_000.0));

    public static EconomyConfig Default { get; } = new();
}
