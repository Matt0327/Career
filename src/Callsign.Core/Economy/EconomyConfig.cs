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

    // --- Base fuel farm (Phase 7g): a capex hub that buys fuel wholesale — legs departing here burn cheaper ---
    public int MaxFuelFarmLevel { get; init; } = 3;
    public long FuelFarmUpgradeCents(int toLevel) => toLevel switch          // one-off capex to reach a level
    {
        1 => 4_000_000, 2 => 12_000_000, 3 => 30_000_000, _ => 0,          // $40k / $120k / $300k
    };
    public double FuelFarmDiscountPct(int level) => level switch             // off the fuel burned on legs departing here
    {
        >= 3 => 0.25, 2 => 0.18, 1 => 0.10, _ => 0,
    };
    public long FuelFarmUpkeepCentsPerDay(int level) => level switch          // the fixed daily cost of running it
    {
        >= 3 => 11_000, 2 => 5_500, 1 => 2_500, _ => 0,                    // $110 / $55 / $25 a day
    };

    // --- Used-aircraft market (Phase 7g): buy pre-owned airframes cheaper, at the cost of hours + condition ---
    public int UsedMarketCount { get; init; } = 6;                            // listings on the used lot at a time
    /// <summary>Cheapest a used airframe goes, as a fraction of new (at the lowest listed condition, 50%).
    /// INVARIANT: this must stay ABOVE <see cref="AircraftResaleFactor"/> (0.70) — otherwise you could buy a
    /// worn bird cheap, restore its condition via a flat-fee service, and resell it at full-condition resale
    /// for a risk-free profit that scales with the aircraft's value. Kept at 0.74 (a comfortable margin).</summary>
    public double UsedMarketFloorFactor { get; init; } = 0.64;
    public double UsedMarketCeilFactor { get; init; } = 0.84;                // a near-pristine used bird tops out here
    /// <summary>What a used airframe costs as a fraction of new, scaled by its condition (0..100000 milli).</summary>
    public double UsedPriceFactor(int conditionMilli)
        => UsedMarketFloorFactor + Math.Clamp(conditionMilli / 100_000.0, 0, 1) * (UsedMarketCeilFactor - UsedMarketFloorFactor);

    // --- Weather (Phase 8): the deterministic synthetic world model (WorldOracle) ---
    /// <summary>How long the weather holds at a field before it re-rolls — keeps a run's conditions plannable.</summary>
    public TimeSpan WeatherWindow { get; init; } = TimeSpan.FromHours(3);
    /// <summary>Size (degrees) of a weather cell — nearby airports inside one cell share a front, not independent noise.</summary>
    public double WeatherCellDeg { get; init; } = 2.5;
    /// <summary>Floor wind speed (kt) — even a calm field has a breath of wind.</summary>
    public int WeatherBaseWindKts { get; init; } = 3;
    /// <summary>The windy-day ceiling (kt) that the tail of the wind distribution reaches for.</summary>
    public int WeatherMaxWindKts { get; init; } = 32;
    /// <summary>Fatness of the wind tail — higher means calm most days with rarer, stronger blows.</summary>
    public double WeatherWindTailExp { get; init; } = 2.4;

    // --- Economy cycle (Phase 8c): a slow boom↔bust that swings what clients pay for work ---
    /// <summary>How long one full boom→bust→recovery cycle takes (real wall-clock, the pace the calendar runs).</summary>
    public TimeSpan EconomyCyclePeriod { get; init; } = TimeSpan.FromDays(45);
    /// <summary>How far job rewards swing across the cycle (±fraction) — a bust pays this much less than the
    /// mean, a boom this much more. Fixed costs (wages, rent, loans) don't move, so a bust squeezes margins.</summary>
    public double EconomyCycleAmplitude { get; init; } = 0.22;

    // --- Client loyalty (Phase 8d): serve a client well and they pay you a repeat premium; burn them and they cool ---
    public const int LoyaltyMax = 100_000;
    /// <summary>Loyalty gained from a clean, full delivery (before the score kicker below).</summary>
    public int ClientLoyaltyFullMilli { get; init; } = 2_500;
    /// <summary>Loyalty lost when a delivery only partly succeeds (damaged freight, an unhappy ride).</summary>
    public int ClientLoyaltyPartialMilli { get; init; } = -1_200;
    /// <summary>Loyalty lost when a delivery fails outright — a burned client cools fast.</summary>
    public int ClientLoyaltyFailedMilli { get; init; } = -6_000;
    /// <summary>Below this loyalty a client pays no premium; above it the repeat premium ramps up.</summary>
    public int ClientLoyaltyBonusThresholdMilli { get; init; } = 25_000;
    /// <summary>The repeat premium a fully-loyal client pays on top of the earned base reward (fraction).</summary>
    public double ClientLoyaltyBonusMaxPct { get; init; } = 0.12;

    /// <summary>How much a delivery moves this client's loyalty. A full delivery builds it (a sharper flight
    /// builds a little more); a partial dings it; a failure sours it hard. Manual/legacy legs (unscored) use
    /// the flat full gain.</summary>
    public int ClientLoyaltyDeltaMilli(MissionGrade grade, bool scored, int score) => grade switch
    {
        MissionGrade.Failed => ClientLoyaltyFailedMilli,
        MissionGrade.Partial => ClientLoyaltyPartialMilli,
        _ => scored
            ? (int)Math.Round(ClientLoyaltyFullMilli * (0.6 + 0.4 * Math.Clamp(score, 0, 100) / 100.0))
            : ClientLoyaltyFullMilli,
    };

    /// <summary>The repeat premium (fraction of the earned base) a client at <paramref name="loyaltyMilli"/>
    /// pays — 0 below the threshold, ramping linearly to <see cref="ClientLoyaltyBonusMaxPct"/> at full loyalty.</summary>
    public double ClientLoyaltyBonusPct(int loyaltyMilli)
    {
        if (loyaltyMilli <= ClientLoyaltyBonusThresholdMilli) return 0;
        double span = Math.Max(1, LoyaltyMax - ClientLoyaltyBonusThresholdMilli);
        double t = Math.Min(1.0, (loyaltyMilli - ClientLoyaltyBonusThresholdMilli) / span);
        return t * ClientLoyaltyBonusMaxPct;
    }

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

    // --- Market pressure (Phase 7g): your own trading moves the local price ---
    /// <summary>The most your own footprint can shove one good's price at one airport (±fraction) once you
    /// have fully cornered or flooded it — layered on top of region bias + window swing. Buying lifts the
    /// price, selling softens it, so the very arbitrage you work erodes as you work it.
    /// INVARIANT: keep this at or below the round-trip dealer cost (2 × <see cref="TradeSpreadPct"/>) so a
    /// same-airport buy-then-sell-back always LOSES. Above ~2×spread, a single large buy lifts that airport's
    /// own sell price past what you paid, turning the mechanic into a risk-free money pump. At 0.08 vs a 0.10
    /// round trip a full-scale round trip still loses ~2.4% of mid — a comfortable margin.</summary>
    public double MarketPressureSwing { get; init; } = 0.08;
    /// <summary>Net weight of a single good (lb) your trades must move at one airport to reach the full
    /// <see cref="MarketPressureSwing"/> — a few loaded runs. Below this the market barely notices you.</summary>
    public int MarketPressureFullScaleLbs { get; init; } = 12_000;
    /// <summary>How fast market pressure mean-reverts to neutral — half of it is gone after this long. An
    /// arbitrage you leave alone heals; one you keep hammering stays suppressed while you work it.</summary>
    public TimeSpan MarketPressureHalfLife { get; init; } = TimeSpan.FromDays(2);

    // --- Contract pricing (Phase 7g): mark up your recurring lines, at the cost of fill rate ---
    /// <summary>The highest markup you can demand on a standing order, in thousandths (1500 = +50%). At or
    /// below the fair rate the client fills every trip; above it, some legs fly empty.</summary>
    public int MaxContractMarkupMilli { get; init; } = 1500;
    /// <summary>How fast the per-trip fill rate falls as you mark up: fill = 1 − sensitivity·(markup−1). At
    /// 0.6, the yield-maximising markup sits near +33% (higher pay per trip, but ~20% of legs run empty).</summary>
    public double ContractDemandSensitivity { get; init; } = 0.6;
    /// <summary>The fill rate never falls below this, however greedy the price — there's always some client.</summary>
    public double ContractFillFloor { get; init; } = 0.25;

    /// <summary>Per-trip probability the client actually ships at your chosen markup. 1.0 at or below the fair
    /// rate; falls linearly above it (a premium leaves some legs empty), clamped to <see cref="ContractFillFloor"/>.</summary>
    public double ContractFillProbability(int priceMultiplierMilli)
    {
        double markup = priceMultiplierMilli / 1000.0;
        if (markup <= 1.0) return 1.0;
        return Math.Clamp(1.0 - ContractDemandSensitivity * (markup - 1.0), ContractFillFloor, 1.0);
    }

    // --- Loans (Phase 4a) ---
    /// <summary>Repayment horizon for a new loan (straight-line principal over this many days).</summary>
    public int LoanTermDays { get; init; } = 90;

    // --- Credit rating (Phase 7g): your track record sets your borrowing terms ---
    /// <summary>The neutral credit score — a company with no history borrows at the tier's listed APR; above
    /// it earns a discount, below it a premium. A fresh company scores exactly this (no adjustment).</summary>
    public int CreditPivotScore { get; init; } = 60;
    /// <summary>Score gained per loan repaid in full (proven reliability), counted up to the cap.</summary>
    public int CreditPaidLoanPoints { get; init; } = 8;
    public int CreditPaidLoanCap { get; init; } = 5;
    /// <summary>A repaid loan only builds credit if it was actually carried this many days — a same-day
    /// take-and-repay (which accrues no interest) proves nothing, so it doesn't count toward your rating.</summary>
    public int CreditSeasoningDays { get; init; } = 14;
    /// <summary>Score lost per loan defaulted on — a heavy, lasting hit to your terms.</summary>
    public int CreditDefaultPenaltyPoints { get; init; } = 25;
    /// <summary>Most score lost to leverage — scales with how much of your (cash + debt) is debt right now.</summary>
    public int CreditMaxLeveragePenaltyPoints { get; init; } = 30;
    /// <summary>Basis points added to (or removed from) a tier's APR per point of score away from the pivot.</summary>
    public int CreditBpsPerScorePoint { get; init; } = 12;
    /// <summary>The APR adjustment band (bps): the most a strong rating discounts, the most a weak one adds.</summary>
    public int CreditAprDeltaMinBps { get; init; } = -400;
    public int CreditAprDeltaMaxBps { get; init; } = 800;
    /// <summary>An APR never drops below this floor, however good the rating.</summary>
    public int CreditAprFloorBps { get; init; } = 100;

    /// <summary>A 0–100 credit score from repayment history and current leverage. Pure; a fresh company
    /// (no repaid/defaulted loans, no outstanding debt) scores exactly <see cref="CreditPivotScore"/>.</summary>
    public int CreditScore(int paidOffLoans, int defaultedLoans, long outstandingCents, long cashCents)
    {
        double score = CreditPivotScore
            + CreditPaidLoanPoints * Math.Min(paidOffLoans, CreditPaidLoanCap)
            - (double)CreditDefaultPenaltyPoints * defaultedLoans;
        if (outstandingCents > 0)
        {
            double denom = Math.Max(0, cashCents) + outstandingCents;
            double debtLoad = denom > 0 ? outstandingCents / denom : 1.0; // fraction of your (cash + debt) that is debt, [0,1]
            score -= CreditMaxLeveragePenaltyPoints * debtLoad;
        }
        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    /// <summary>The APR adjustment (bps) for a score: negative discounts a strong rating, positive a weak one.</summary>
    public int CreditAprDeltaBps(int score)
        => Math.Clamp((CreditPivotScore - score) * CreditBpsPerScorePoint, CreditAprDeltaMinBps, CreditAprDeltaMaxBps);

    /// <summary>A tier's base APR adjusted for the score, never below <see cref="CreditAprFloorBps"/>.</summary>
    public int EffectiveAprBps(int baseAprBps, int score)
        => Math.Max(CreditAprFloorBps, baseAprBps + CreditAprDeltaBps(score));

    // --- Loan default (Phase 7g): the safety valve when a company can't service its debt ---
    /// <summary>Cash below which a company is treated as unable to service its loans this period, tipping it
    /// into forbearance. At 0, any negative balance after all of the period's income and costs counts.</summary>
    public long LoanDelinquencyCashFloorCents { get; init; } = 0;
    /// <summary>How long a loan can sit in forbearance (the company underwater) before it defaults — the grace
    /// window the player is warned across every reconcile before the write-off executes (Law 4).</summary>
    public int LoanDefaultGraceDays { get; init; } = 30;

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
