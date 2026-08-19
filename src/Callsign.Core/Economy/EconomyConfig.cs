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

    // --- Phase 7c: the composite flight score is the lever for a tracker-flown leg (replacing raw fpm) ---

    /// <summary>Reward multiplier delta from the whole-flight score (landing ∧ approach ∧ enroute): an
    /// excellent, clean flight earns a bonus; a sloppy or dangerous one a penalty. Decimal for the
    /// away-from-zero cents rounding. An invalidated (cheated) flight is capped to ≤ 0 at settlement.</summary>
    public decimal PerformancePct(int overallScore) =>
        overallScore >= 95 ? 0.15m : overallScore >= 85 ? 0.10m : overallScore >= 70 ? 0.05m
      : overallScore >= 50 ? 0.00m : overallScore >= 35 ? -0.05m : overallScore >= 20 ? -0.15m : -0.30m;

    /// <summary>XP multiplier from the flight score — a great flight grows the pilot faster, a poor one
    /// slower. Ranges 0.5×…1.25× (1.0× at ~score 67).</summary>
    public double ScoreXpMultiplier(int overallScore) => Math.Clamp(0.5 + 0.0075 * overallScore, 0.5, 1.25);

    /// <summary>A small reputation nudge from how the leg was flown, on top of the mission's own reward.
    /// A cheated (invalid) flight costs the most.</summary>
    public int ScoreReputationMilli(int overallScore, bool valid) =>
        !valid ? -800 : overallScore >= 90 ? 300 : overallScore < 35 ? -600 : 0;

    /// <summary>Reputation hit for a failed delivery (Phase 7d) — a destroyed load or a lost client.</summary>
    public int FailedDeliveryReputationMilli { get; init; } = -1_000;

    // --- Delivery clock (Phase 7d): time-critical missions freeze a deadline at accept ---
    public double DeadlineNominalCruiseKts { get; init; } = 140;  // a typical light-twin cruise, for the estimate
    public double ExpressClockSlack { get; init; } = 1.6;         // 60% margin over a nominal direct flight
    public double EmergencyClockSlack { get; init; } = 1.35;      // tighter — relief can't wait

    /// <summary>The delivery deadline for a time-critical mission, or null if the type has no clock.</summary>
    public DateTimeOffset? MissionDeadline(MissionType type, double distanceNm, DateTimeOffset acceptedAt)
    {
        double? slack = type switch
        {
            MissionType.Express => ExpressClockSlack,
            MissionType.Emergency => EmergencyClockSlack,
            _ => null,
        };
        if (slack is not double s) return null;
        double hours = distanceNm / Math.Max(60, DeadlineNominalCruiseKts) * s;
        return acceptedAt.AddHours(hours);
    }

    /// <summary>Continuous hull wear from the touchdown — scales with the worst-of-three sink rate over a
    /// 200 fpm floor and with peak g over 1.4, so a firm arrival wears the airframe proportionally
    /// (replacing the old binary hard-landing step for a tracker-flown leg).</summary>
    public int LandingWearFpmK { get; init; } = 500;   // hull wear milli per 100 fpm over the 200 fpm floor
    public int LandingWearGK { get; init; } = 1_000;   // hull wear milli per g over 1.4

    public int LandingWearMilli(double touchdownFpmWorst3, double touchdownG) =>
        (int)Math.Round(LandingWearFpmK * Math.Max(0, Math.Abs(touchdownFpmWorst3) - 200) / 100.0)
      + (int)Math.Round(LandingWearGK * Math.Max(0, touchdownG - 1.4));

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
    public long FuelPriceCentsPerLb { get; init; } = 90;           // ~$0.90/lb of consumable fuel burned (Phase 7e)

    // --- Airworthiness & inspections (Phase 7e): a worn or overdue tail is grounded until serviced ---
    public int AirworthyFloorMilli { get; init; } = 20_000;        // below 20% hull/engine condition = grounded
    public double HundredHourIntervalHours { get; init; } = 100;   // a 100-hour inspection comes due every 100 airframe hours
    public double AnnualIntervalDays { get; init; } = 365;         // an annual inspection every 12 months
    public long HundredHourInspectionCents { get; init; } = 80_000;   // $800 for a 100-hour
    public long AnnualInspectionCents { get; init; } = 250_000;       // $2,500 for an annual

    // --- Crew skill on autonomous trips (Phase 7f): a green pilot has more incidents than an ace ---
    public double BaseIncidentRatePct { get; init; } = 0.06;      // incident chance per trip at 0% skill
    public double IncidentSkillExponent { get; init; } = 2.0;     // p = base·(1-skill)^exp — skill bites hard
    // An incident lands on a severity tier: mostly minor, sometimes a diversion, rarely a lost trip.
    public double IncidentMajorShare { get; init; } = 0.08;       // of incidents — a lost trip (no pay, heavy wear)
    public double IncidentDiversionShare { get; init; } = 0.27;   // of incidents — a diversion (half pay); rest are minor
    public double IncidentMinorDockPct { get; init; } = 0.10;     // a minor scuff shaves a little
    public double IncidentDiversionDockPct { get; init; } = 0.50; // a diversion delivers half
    public int IncidentMinorWearMilli { get; init; } = 300;
    public int IncidentDiversionWearMilli { get; init; } = 800;
    public int IncidentMajorWearMilli { get; init; } = 2_500;
    public double MaxDutyHoursPerDay { get; init; } = 8;          // FTL: one crew flies ~8 of 24 h — own crew depth to run a tail harder
    public int CrewProficiencyGainMilliPerTrip { get; init; } = 40;  // a hired pilot sharpens ~0.04%/trip flown — hire green cheap, they improve
    public int CrewSkillCeilingMilli { get; init; } = 95_000;        // experience tops out at 95% (nobody is perfect)

    /// <summary>Skill a hired pilot needs to be trusted with a category (their "type rating", Phase 7f) — a
    /// green crew flies light singles; bigger, faster iron demands a sharper pilot. Assignment is gated on it.</summary>
    public int MinSkillMilliForCategory(AircraftCategory category) => category switch
    {
        AircraftCategory.LightTwin => 35_000,
        AircraftCategory.Helicopter => 45_000,
        AircraftCategory.Turboprop => 50_000,
        AircraftCategory.LightJet => 65_000,
        AircraftCategory.Jet => 78_000,
        AircraftCategory.Heavy => 88_000,
        _ => 0, // light single / glider / unknown — anyone can be trusted with it
    };

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

    // --- Base maintenance shop (Phase 7g): a capex hub that discounts servicing your fleet based there ---
    public int MaxMaintenanceShopLevel { get; init; } = 3;
    public long MaintenanceShopUpgradeCents(int toLevel) => toLevel switch  // one-off capex to reach a level
    {
        1 => 5_000_000, 2 => 15_000_000, 3 => 40_000_000, _ => 0,          // $50k / $150k / $400k
    };
    public double MaintenanceShopDiscountPct(int level) => level switch     // off maintenance + inspections here
    {
        >= 3 => 0.35, 2 => 0.25, 1 => 0.15, _ => 0,
    };
    public long MaintenanceShopUpkeepCentsPerDay(int level) => level switch  // the fixed daily cost of running it
    {
        >= 3 => 15_000, 2 => 7_000, 1 => 3_000, _ => 0,                    // $150 / $70 / $30 a day
    };

    // --- Trade (Phase 2g): buy low here, sell high there ---
    /// <summary>How far a good's price swings above/below its catalog base across airports (±fraction).</summary>
    public double TradePriceSwing { get; init; } = 0.35;
    /// <summary>Structural regional bias (Phase 7g): a FIXED per-airport export/import tilt per good — some
    /// places always produce a good cheap, others always demand it dear — layered under the window swing so
    /// commodity routes have a learnable shape, not just noise.</summary>
    public double RegionBiasSwing { get; init; } = 0.25;
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

    // --- Ferry / relocate (Phase 6 hangar) — positioning an idle airframe between fields ---
    public long AircraftFerryBaseCents { get; init; } = 30_000;  // $300 to move a tail at all
    public long AircraftFerryPerNmCents { get; init; } = 350;    // $3.50 per nm ferried

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
