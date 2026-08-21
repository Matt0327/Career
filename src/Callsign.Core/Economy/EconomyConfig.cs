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

    // Engine-abuse wear (Phase 9e, on the Fun Dial): the sim's OWN cumulative engine-damage model is the
    // authority — a leg's accrued damage-percent points (max − baseline) become engine-condition loss here,
    // which then bills through the existing maintenance visits + resale + insurance paths. Never a per-
    // exceedance cash hit: a brief harmless exceedance leaves the sim's damage untouched → zero cost; only
    // genuine, sim-modelled harm accrues, warned first in the live log (L4). At 1500, 1% of engine damage
    // costs 1.5% of engine condition, so a destroyed engine (~100%) fully wears it down to an overhaul.
    public int EngineAbuseWearMilliPerPct { get; init; } = 1_500;
    public int EngineAbuseWearMilli(double pctAccrued) => (int)Math.Round(Math.Max(0, pctAccrued) * EngineAbuseWearMilliPerPct);

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

    // --- Airline operating reputation (Phase 11a) — the operation's own name, distinct from pilot reputation.
    // Money-neutral in 11a: these tunables move Company.OperatingReputationMilli only; no cash path is touched.
    // L12: the name moves TOWARD the competence of the crew you chose (autonomous legs) and by your own
    // telemetry score (player legs), so it can never be farmed above the operation you actually run. ---
    public const int OperatingReputationMax = 100_000;               // 0..100.0, same scale as pilot rep
    public double OperatingRepConvergePerTrip { get; init; } = 0.04; // fraction of the (crewSkill − rep) gap closed per trip
    public int OperatingRepAutoMaxStepPerPassMilli { get; init; } = 1_500; // most one reconcile pass moves it (±1.5 pts) — no teleport
    public int OperatingRepPlayerHighScore { get; init; } = 90;      // at/above → the name builds
    public int OperatingRepPlayerLowScore { get; init; } = 40;       // below → the name dings
    public int OperatingRepPlayerGainMilli { get; init; } = 250;     // a great player leg builds the name
    public int OperatingRepPlayerDingMilli { get; init; } = -400;    // a poor leg costs more than a great one gains
    public int OperatingRepPlayerCheatMilli { get; init; } = -800;   // an invalidated (cheated) leg is the worst move

    /// <summary>Player-flown scored leg → airline-rep move. A cheated leg costs the most; a great leg builds;
    /// an ordinary leg (in the coaching band) neither builds nor dings (L9); an unscored legacy/manual leg
    /// moves nothing (L10 — no score, no judgement).</summary>
    public int OperatingRepPlayerDeltaMilli(int overallScore, bool scored, bool valid)
        => !scored ? 0
         : !valid  ? OperatingRepPlayerCheatMilli
         : overallScore >= OperatingRepPlayerHighScore ? OperatingRepPlayerGainMilli
         : overallScore <  OperatingRepPlayerLowScore  ? OperatingRepPlayerDingMilli
         : 0;

    /// <summary>The fraction of the gap toward crew competence a batch of <paramref name="trips"/> closes this
    /// pass — proportional to trips, saturating at 1.0 (a single batch never moves PAST crew skill). Also the
    /// weight the caller uses to derive the trip-weighted convergence target when several lines fly. Pure.</summary>
    public double OperatingRepConvergeFrac(int trips)
        => trips <= 0 ? 0.0 : Math.Min(1.0, OperatingRepConvergePerTrip * trips);

    /// <summary>Self-balancing pull of one batch of <paramref name="trips"/> flown by a
    /// <paramref name="crewSkillMilli"/> crew on an airline currently at <paramref name="currentRepMilli"/>:
    /// a fraction of the gap TOWARD crew competence, proportional to trips (a poorer crew than your name pulls
    /// it DOWN — the L12 cap). The CALLER sums pulls across all batches in a pass, caps the net step to
    /// ±<see cref="OperatingRepAutoMaxStepPerPassMilli"/>, AND bounds the result to the trip-weighted crew-skill
    /// target (via <see cref="OperatingRepConvergeFrac"/>) so the summed move never overshoots the operation
    /// that flew. Pure.</summary>
    public int OperatingRepCrewPullMilli(int currentRepMilli, int crewSkillMilli, int trips)
        => (int)Math.Round((crewSkillMilli - currentRepMilli) * OperatingRepConvergeFrac(trips));

    // --- Airline reputation → hub demand (Phase 11c): your operating name PAYS at the fields you've based at.
    // Reputation (Company.OperatingReputationMilli, 0..100_000) lifts income-only surfaces at your HUBS:
    // job pay + job offer count on a board originating at a base, and the frozen per-trip pay of a route /
    // standing order flown from one. Frozen at posting (jobs) / creation (routes+orders) exactly like the 8c
    // DemandMult, so settlement/reconcile read the frozen rate and pumping the name after accepting changes
    // nothing owed (L5/L6). It stacks multiplicatively on the DemandMult (a boom and a strong name compound).
    //
    // INVARIANT (why no spread cap, unlike WeatherDemandSwing / MarketPressureSwing): every lifted surface is
    // income-only — a one-way delivery fee (jobs) or a one-directional frozen annuity (routes/orders) — with NO
    // buy-back and NO counterparty, so no same-field round trip exists to pump and NO ≤2×spread ceiling is
    // needed. Reputation is NEVER passed to MarketService.Quote (its signature has no rep parameter — a compile-
    // time guarantee), so the two-sided commodity market's WeatherDemandSwing (0.10) ≤ 2×TradeSpreadPct (0.10)
    // guard is preserved BYTE-IDENTICALLY: rep contributes exactly 0.0 to every buy/sell there. Weather is read
    // ONLY by the market, rep ONLY by these income surfaces — they live on disjoint surfaces and never stack.
    //
    // NEUTRAL FLOOR (L8): HubReputationPayFactor(0) == 1.0 and HubReputationOfferCount(c,0) == c. Reputation 0,
    // OR an off-hub board, OR a company with no bases → byte-identical to pre-11c economics. A low name earns a
    // SMALLER lift down to the 1.0x baseline, never a penalty below it. ---

    /// <summary>The most a flawless (100.0) operating reputation lifts PAY at your hubs (fraction on the frozen
    /// base reward). In family with its reward-bonus siblings (weather +0.10, loyalty +0.12, comfort +0.08).</summary>
    public double HubReputationPaySwing { get; init; } = 0.15;

    /// <summary>The most a flawless operating reputation widens a hub's job board (fraction of extra offers) — a
    /// strong name draws MORE work to the fields you've invested in. NON-MONEY: count is offer QUANTITY, not
    /// per-unit pay; each extra offer still requires a flown, telemetry-scored leg to earn, and the board is
    /// destructively regenerated each refresh (holds ≤ the computed count, never accumulating), so a generous
    /// swing is pump-free. Neutral (×1) at reputation 0 and off-hub.</summary>
    public double HubReputationCountSwing { get; init; } = 0.50;

    /// <summary>The shared 0→1 reputation ramp: 0.0 at reputation 0 (the neutral floor), linear to 1.0 at 100.0.
    /// Every hub-reputation lift rides this one shape, so they build together and neutralise together. Pure.</summary>
    private double HubReputationRamp(int reputationMilli)
        => Math.Clamp(reputationMilli / (double)OperatingReputationMax, 0.0, 1.0);

    /// <summary>The PAY lift (×) for a job originating at a hub, or a route/standing order flown from one, at this
    /// operating reputation — 1.0 at rep 0 (neutral), ramping to 1 + <see cref="HubReputationPaySwing"/> at 100.0.
    /// A pure rep→factor function: the CALLER does the hub scoping and passes reputation 0 for an off-hub surface,
    /// which prices it unchanged. <paramref name="hubLevel"/> (Phase 11e) amplifies the swing at an upgraded hub;
    /// 0 (a plain base) is byte-identical to 11c. Frozen into the reward at posting/creation.</summary>
    public double HubReputationPayFactor(int reputationMilli, int hubLevel = 0)
        => 1.0 + HubReputationPaySwing * (1.0 + hubLevel * HubLevelAmplification) * HubReputationRamp(reputationMilli);

    /// <summary>The reputation-widened offer count for a hub board — the caller's base count at rep 0, ramping to
    /// (1 + <see cref="HubReputationCountSwing"/>)× at full reputation, rounded to whole offers. <paramref name="hubLevel"/>
    /// (Phase 11e) amplifies the widening at an upgraded hub; 0 is byte-identical to 11c. Pure.</summary>
    public int HubReputationOfferCount(int baseCount, int reputationMilli, int hubLevel = 0)
        => (int)Math.Round(baseCount * (1.0 + HubReputationCountSwing * (1.0 + hubLevel * HubLevelAmplification) * HubReputationRamp(reputationMilli)));

    // --- Contract on-ramp (Phase 11d): flying the majors' overflow — the distinct flavour of the bottom rung.
    // A contract-carrier job is an ordinary Cargo job (existing settlement path, no new money seam) priced BELOW
    // the freelance owner rate. It is the safe, lower-yield early option, never a higher-yield idle pump. ---
    /// <summary>What a contract-carrier job pays as a fraction of the freelance owner rate for the same leg — the
    /// on-ramp's price. INVARIANT: strictly below 1.0 (the owner margin it substitutes), with enough headroom that
    /// even a maxed loyalty premium (<see cref="ClientLoyaltyBonusPct"/> ≤ ~0.12) keeps it under 1.0 — so the safe,
    /// reliable contract option always yields LESS than flying your own freight. 0.72 × 1.12 = 0.806 &lt; 1.0.
    /// The contract rate also never takes the 11c hub-reputation lift (the carrier's rate is fixed, not yours).</summary>
    public double ContractCarrierPayFactor { get; init; } = 0.72;

    /// <summary>The contract on-ramp's share of the job board (a modest weight beside the mission roster), so
    /// overflow work is a visible minority of a beginner's options, not the whole board.</summary>
    public double ContractCarrierBoardShare { get; init; } = 0.7;

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

    // --- Base hub (Phase 11e): a capex facility that AMPLIFIES the operating-reputation demand lift (11c) at this
    // field — a hub you've invested in draws your name's premium harder, on jobs AND routes departing here. Level 0
    // (a plain base) is byte-identical to 11c; each level scales the reputation swing by HubLevelAmplification. ---
    public int MaxHubLevel { get; init; } = 3;
    public long HubUpgradeCents(int toLevel) => toLevel switch               // one-off capex — premium, because it lifts income
    {
        1 => 8_000_000, 2 => 24_000_000, 3 => 60_000_000, _ => 0,          // $80k / $240k / $600k
    };
    public long HubUpkeepCentsPerDay(int level) => level switch              // the fixed daily cost of running the hub
    {
        >= 3 => 30_000, 2 => 14_000, 1 => 6_000, _ => 0,                   // $300 / $140 / $60 a day
    };
    /// <summary>How much each hub level amplifies the 11c reputation swing: the effective swing is
    /// <c>baseSwing × (1 + hubLevel × HubLevelAmplification)</c>. At L3 that is 2.5× the plain-base swing.</summary>
    public double HubLevelAmplification { get; init; } = 0.5;

    // --- Scheduled passenger network (Phase 11f): the AOC-gated top-tier route variant. A scheduled route's
    // per-trip revenue = seats × load factor × per-seat yield, frozen at creation and booked by the EXISTING
    // reconcile route loop (no new settlement path). Load factor is driven by the airline's operating reputation
    // — a stronger name fills more seats — the flywheel's payoff at the top of the ladder. ---
    public int ScheduledMinSeats { get; init; } = 30;                  // needs an airliner, not a light twin
    /// <summary>A scheduled seat prices BELOW a private-charter seat (lower margin, far higher volume).</summary>
    public double ScheduledYieldFactor { get; init; } = 0.55;
    /// <summary>The per-seat revenue for one scheduled round trip — the charter per-seat component (excluding the
    /// dispatch fee), scaled by <see cref="ScheduledYieldFactor"/>. Grows with distance. Frozen at creation.</summary>
    public long ScheduledSeatYieldCents(double distanceNm)
        => (long)Math.Round((PaxPerPaxCents + distanceNm * PaxPerPaxNmCents) * ScheduledYieldFactor);
    public int ScheduledBaseLoadFactorMilli { get; init; } = 650;      // 65% of seats fill at a no-name operation
    public int ScheduledLoadFactorRepBonusMilli { get; init; } = 250;  // a flawless operating name fills +25 points more
    public int ScheduledMaxLoadFactorMilli { get; init; } = 920;       // cap 92% — never a guaranteed full plane
    /// <summary>The seat load factor (thousandths) frozen onto a scheduled route at creation: a base fill lifted
    /// by the operating reputation, capped. A strong name is worth real money here — the top of the flywheel.</summary>
    public int ScheduledLoadFactorMilli(int reputationMilli)
        => Math.Min(ScheduledMaxLoadFactorMilli,
                    ScheduledBaseLoadFactorMilli + (int)Math.Round(ScheduledLoadFactorRepBonusMilli * HubReputationRamp(reputationMilli)));

    // --- Used-aircraft market (Phase 7g): buy pre-owned airframes cheaper, at the cost of hours + condition ---
    public int UsedMarketCount { get; init; } = 6;                            // listings on the used lot at a time
    /// <summary>The condition-0 INTERCEPT of <see cref="UsedPriceFactor"/>, as a fraction of new — NOT the
    /// cheapest a bird actually sells for. Used listings are only ever generated at 50%–95% condition
    /// (AircraftDealerService.MakeUsedListing), so the cheapest a used airframe is actually LISTED is
    /// UsedPriceFactor(0.50) = 0.64 + 0.50·(0.84−0.64) = 0.74× new.
    /// INVARIANT: that EFFECTIVE floor (0.74) must stay ABOVE <see cref="AircraftResaleFactor"/> (0.70), or you
    /// could buy a worn bird cheap, restore its condition with a flat-fee service, and resell at full-condition
    /// resale for a risk-free profit. At 0.74 vs 0.70 that buy→restore→resell round trip loses ~4% before the
    /// service fee — so if you retune either factor, keep UsedPriceFactor(0.50) &gt; AircraftResaleFactor.</summary>
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

    // --- Weather → market demand (Phase 8f): foul conditions lift a field's commodity prices ---
    /// <summary>The most foul weather lifts a field's commodity prices (fraction on the mid) — supply is
    /// disrupted and local demand spikes, so a storm-hit field pays dearer to sell into (and costs more to buy).
    /// INVARIANT: keep this at or below the round-trip dealer cost (2 × <see cref="TradeSpreadPct"/>). The lift is
    /// ONE-SIDED (weather only ever raises the price), and foul weather is a recurring event, so a larger swing
    /// would let a patient trader buy a good in clear weather, hold it at the field, and sell it once a storm
    /// arrives for a risk-free same-field profit. At/below 2×spread that round trip always loses.</summary>
    public double WeatherDemandSwing { get; init; } = 0.10;
    /// <summary>At or above this visibility (sm) the weather is "clear" and lifts nothing; the lift ramps in as
    /// visibility falls toward zero.</summary>
    public double WeatherDemandClearVisSm { get; init; } = 6.0;

    // --- Weather → autonomous scrubs (Phase 8f-2): foul weather grounds an autonomous trip ---
    /// <summary>Below this visibility (sm) at the origin, an autonomous trip is weathered out — scrubbed for
    /// that slot (no income, no wear), though the time still passes. Fog (0.4 sm) trips it; rain/snow don't.</summary>
    public double WeatherScrubVisSm { get; init; } = 1.0;
    /// <summary>At or above this wind (kt) at the origin, an autonomous trip is scrubbed — beyond safe limits
    /// for an unattended departure.</summary>
    public int WeatherScrubWindKts { get; init; } = 35;

    /// <summary>Whether the origin weather scrubs an autonomous trip (Phase 8f-2): too little visibility or too
    /// much wind to launch. A pure threshold, so the scrub is deterministic in (place, time).</summary>
    public bool WeatherScrubsTrip(double visibilitySm, int windKts)
        => visibilitySm < WeatherScrubVisSm || windKts >= WeatherScrubWindKts;

    /// <summary>The local-demand price factor from the weather (Phase 8f): 1.0 in clear air, ramping to
    /// 1 + <see cref="WeatherDemandSwing"/> as visibility falls to zero. A pure function of visibility, so a
    /// caller with no weather data (factor left at 1.0) prices the market unchanged.</summary>
    public double WeatherDemandFactor(double visibilitySm)
    {
        double clear = Math.Max(0.1, WeatherDemandClearVisSm);
        double badness = Math.Clamp((clear - visibilitySm) / clear, 0.0, 1.0);
        return 1.0 + WeatherDemandSwing * badness;
    }

    /// <summary>Phase 12 — a small, bounded seasonal tilt on job demand (the world feels alive across a career):
    /// winter's freight/heating/holiday logistics pay a touch more, high summer a touch less. Applied to the job
    /// board's demand alongside the macro boom/bust cycle and frozen at posting, so it never re-rates an accepted
    /// job. Income-only (jobs are one-way fees) and deliberately gentle, so it colours the year without swinging
    /// balance. Season comes from <see cref="World.GameCalendar.Season"/> (hemisphere-correct).</summary>
    public double SeasonalDemandFactor(string season) => season switch
    {
        "Winter" => 1.06,   // heating fuel, holiday freight, mail rush
        "Autumn" => 1.03,   // harvest + pre-winter stocking
        "Summer" => 0.97,   // the quiet season
        _ => 1.00,          // Spring — neutral
    };

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
    /// <summary>How fast a client you stop flying for cools (Phase 8d-2): loyalty halves each half-life of
    /// neglect, decaying toward zero, so the premium is a relationship you MAINTAIN, not a one-time unlock.</summary>
    public TimeSpan ClientLoyaltyHalfLife { get; init; } = TimeSpan.FromDays(30);

    /// <summary>A client's loyalty after <paramref name="elapsed"/> of neglect — exponential decay toward zero,
    /// half gone each <see cref="ClientLoyaltyHalfLife"/>. Pure and deterministic, so it's applied on read (never
    /// persisted between deliveries), exactly like the market's pressure decay.</summary>
    public int DecayedLoyaltyMilli(int storedMilli, TimeSpan elapsed)
    {
        if (storedMilli <= 0 || elapsed <= TimeSpan.Zero) return Math.Max(0, storedMilli);
        double hl = Math.Max(1, ClientLoyaltyHalfLife.Ticks);
        double factor = Math.Pow(0.5, elapsed.Ticks / hl);
        return (int)Math.Round(storedMilli * factor);
    }

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

    // --- Passenger comfort (Phase 10c): a smooth PASSENGER ride earns a comfort TIP on top of the earned base
    // (the reward-side mirror of the 7d ride-quality penalty), and builds the client bond a little more. On the
    // Fun Dial (L9): it's pure upside for a smooth ride — a rough one simply earns no tip, base pay stands. ---
    public int ComfortBonusThresholdScore { get; init; } = 75;  // below this comfort score, no tip
    public double ComfortBonusMaxPct { get; init; } = 0.08;     // the tip at a flawless (100) ride, fraction of the earned base
    public int ComfortLoyaltyBonusMilli { get; init; } = 1_200;  // extra client loyalty at a flawless passenger ride
    public int ComfortLoyaltyPenaltyMilli { get; init; } = 1_500;// loyalty a genuinely rough passenger ride sheds

    /// <summary>The comfort tip (fraction of the earned base) a passenger leg with this comfort score pays —
    /// 0 below the threshold, ramping linearly to <see cref="ComfortBonusMaxPct"/> at a flawless 100.</summary>
    public double ComfortBonusPct(int comfortScore)
    {
        if (comfortScore <= ComfortBonusThresholdScore) return 0;
        double span = Math.Max(1, 100 - ComfortBonusThresholdScore);
        return Math.Min(1.0, (comfortScore - ComfortBonusThresholdScore) / span) * ComfortBonusMaxPct;
    }

    /// <summary>The loyalty a passenger ride moves purely on comfort: a smooth ride (≥ threshold) adds up to
    /// <see cref="ComfortLoyaltyBonusMilli"/>; a rough one (&lt; 50) sheds up to <see cref="ComfortLoyaltyPenaltyMilli"/>.</summary>
    public int ComfortLoyaltyDeltaMilli(int comfortScore)
    {
        if (comfortScore >= ComfortBonusThresholdScore)
            return (int)Math.Round(ComfortLoyaltyBonusMilli * Math.Min(1.0, (comfortScore - ComfortBonusThresholdScore) / Math.Max(1.0, 100.0 - ComfortBonusThresholdScore)));
        if (comfortScore < 50)
            return -(int)Math.Round(ComfortLoyaltyPenaltyMilli * Math.Min(1.0, (50 - comfortScore) / 50.0));
        return 0;
    }

    // --- Operating certificate renewal (Phase 8e-2): warn before the licence lapses (Law 4) ---
    /// <summary>Reconcile nudges you to renew an operating certificate once it's within this many days of
    /// expiring — a proactive Law-4 warning, so a lapse never springs on you and gated routes never silently
    /// stop.</summary>
    public int CertRenewalWarnDays { get; init; } = 21;

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

    // --- Aircraft rental (Phase 9f-1): rent a non-owned tail short-term, fly it BY HAND, return it. Priced so
    // renting is never cheaper than owning per flight-hour (usage rent sits above the wear it substitutes), and
    // the deposit caps the renter's damage liability. The lessor eats routine maintenance; the deposit is touched
    // only by real, telemetry-verified damage beyond ordinary hours-depreciation — so coach-band flying is free. ---
    public int RentFlightHourRateBps { get; init; } = 40;   // usage rent per flight-hour, bps of sticker (0.40%/flt-hr)
    public int RentHoldingRateBps { get; init; } = 20;      // holding fee per calendar day, bps of sticker (0.20%/day)
    public int RentDepositFactor { get; init; } = 15_000;   // escrow = 15% of sticker (milli) — the renter's MAX damage liability
    public int RentTermDefaultDays { get; init; } = 7;      // default short-term window
    public int RentMaxTermDays { get; init; } = 30;         // rentals are short-term
    public int RentExpiryWarnDays { get; init; } = 3;       // digest nudge before auto-return (Law 4)
    public int RentalDeliveryConditionMilli { get; init; } = 100_000; // the world delivers a freshly serviced tail
    // The Fun-Dial forgiveness band on a rental return (L9): ordinary handling wear a hand-flown leg leaves
    // BEYOND the hours line — a firm-but-acceptable landing (LandingWearMilli, ~1000/leg at 400 fpm) and minor
    // engine wear — is expected and free. Only wear PAST this allowance (a real slam, or gross engine abuse from
    // 9e) bites the deposit. Folded into the expected-wear baseline; also absorbs per-leg rounding drift.
    public int RentOrdinaryHandlingWearMilliPerHour { get; init; } = 1_200;

    // usage rent (40 bps) > the resale value ordinary wear/hr consumes (≈ AircraftResaleFactor × wear/hr = 0.70 × 0.4% = 0.28%),
    // so renting a flight-hour always costs more than owning it — no rent-to-earn pump at any utilization.
    public long RentFlightHourCents(long stickerCents) => (long)Math.Round(stickerCents * (RentFlightHourRateBps / 10_000.0));
    public long RentHoldingPerDayCents(long stickerCents) => (long)Math.Round(stickerCents * (RentHoldingRateBps / 10_000.0));
    public long RentDepositCents(long stickerCents) => (long)Math.Round(stickerCents * (RentDepositFactor / 100_000.0));

    // --- Aircraft LEASE (Phase 9f-2): the mid-game capital instrument between "buy used" and "take a loan".
    // A fixed weekly rate (decoupled from usage), a lessee-carried hull-cover line, a minimum term, and a
    // rent-to-own buyout priced at the tail's OWN used-lot value — never below the used floor, so exercising-
    // then-flipping always loses. Priced a deliberate premium over financing so leasing never beats owning. ---
    public int LeaseWeeklyRateBps { get; init; } = 45;      // 0.45%/wk of sticker (~23.4%/yr) — above loan APR
    public int LeaseMinTermDays { get; init; } = 28;        // a lease is a commitment
    public int LeaseMaxTermDays { get; init; } = 336;       // 48 weeks
    public int LeaseUpfrontWeeks { get; init; } = 3;        // first weeks paid at signing — sign-and-abandon is never free
    public int LeaseDepositFactor { get; init; } = 8_000;   // escrow = 8% of sticker (milli) — sized for an ORDINARY return shortfall
    public int LeaseInsuranceWeeklyBps { get; init; } = 32; // lessee-carried hull cover, 0.32%/wk (owner economics: 40bps x 80%)
    public int LeaseCasualtyDeductibleFactor { get; init; } = 8_000; // lessee's bounded write-off cost (8% of sticker, milli)
    public double LeaseRentCreditFactor { get; init; } = 0.50;       // half of paid rent credits a buyout
    public int LeaseBuyoutConditionFloorMilli { get; init; } = 50_000; // the landmine: buyout priced through UsedPriceFactor(>=50%) => >=0.74x market > AircraftResaleFactor
    public int LeaseExpiryWarnDays { get; init; } = 14;     // digest nudge before a lease auto-returns (Law 4)

    /// <summary>The commitment discount on the weekly rate — longer terms are cheaper, capped so the rate stays above ownership carry.</summary>
    public double LeaseLongTermDiscountPct(int termDays) => termDays switch { >= 336 => 0.15, >= 168 => 0.10, >= 84 => 0.05, _ => 0.0 };
    public long LeaseWeeklyRateCents(long stickerCents, int termDays)
        => (long)Math.Round(stickerCents * (LeaseWeeklyRateBps / 10_000.0) * (1 - LeaseLongTermDiscountPct(termDays)));
    public long LeaseInsuranceWeeklyCents(long stickerCents) => (long)Math.Round(stickerCents * (LeaseInsuranceWeeklyBps / 10_000.0));
    public long LeaseDepositCents(long stickerCents) => (long)Math.Round(stickerCents * (LeaseDepositFactor / 100_000.0));
    public long LeaseCasualtyDeductibleCents(long stickerCents) => (long)Math.Round(stickerCents * (LeaseCasualtyDeductibleFactor / 100_000.0));

    /// <summary>The rent-to-own buyout price: the tail's used-lot value (condition floored at 50%), minus a
    /// rent credit, but NEVER below UsedPriceFactor(50%)xmarket = 0.74xmarket > AircraftResaleFactor(0.70), so a
    /// buyout-then-sell always loses (the landmine guard).</summary>
    public long LeaseBuyoutCents(long marketCents, int minConditionMilli, long rentCreditedCents)
    {
        long gross = (long)Math.Round(marketCents * UsedPriceFactor(Math.Max(minConditionMilli, LeaseBuyoutConditionFloorMilli)));
        long credit = (long)Math.Round(rentCreditedCents * LeaseRentCreditFactor);
        long floor = (long)Math.Round(marketCents * UsedPriceFactor(LeaseBuyoutConditionFloorMilli));
        return Math.Max(floor, gross - credit);
    }

    // --- Live weather (Phase 9b): OPTIONAL real METAR above the synthetic model. Master default OFF ⇒
    // EconomyConfig.Default is byte-identical to Phase 8 (NullWeatherSource ⇒ every surface reads synthetic).
    // "Off" and "feed down" are the same code path; live never gates a flight/contract/launch (L8/L10). ---
    public bool LiveWeatherEnabled { get; init; } = false;      // master; OFF ⇒ all reads synthetic
    public bool LiveWeatherFeedsMarket { get; init; } = false;  // even when master ON, the market stays synthetic unless this is ON (9b-2 opt-in)
    public TimeSpan LiveWeatherTtl { get; init; } = TimeSpan.FromMinutes(30);  // reuse a cached obs within this window
    public TimeSpan LiveWeatherMaxAge { get; init; } = TimeSpan.FromMinutes(90); // an obs older than this ⇒ miss ⇒ synthetic (never stale-as-live for mechanics)
    public TimeSpan LiveWeatherTimeout { get; init; } = TimeSpan.FromSeconds(3); // hard per-fetch cap ⇒ timeout falls to synthetic
    public double LiveWeatherMaxStationNm { get; init; } = 30;  // nearest reporting station cap; beyond ⇒ synthetic
    public string LiveWeatherBaseUrl { get; init; } = "https://aviationweather.gov/api/data/";
    public string LiveWeatherUserAgent { get; init; } = "Callsign/9b (+https://callsign.app)"; // the API filters generic UAs

    public static EconomyConfig Default { get; } = new();
}
