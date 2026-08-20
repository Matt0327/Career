namespace Callsign.Core.Flight;

/// <summary>Severity of a flight event, for the live event log.</summary>
public enum FlightEventSeverity
{
    Info,
    Success,
    /// <summary>A friendly, UNSCORED nudge (Phase 9 — the Fun Dial, law L9): a minor deviation below the
    /// violation thresholds. Coaches the pilot without any score or economic effect — warn, don't punish.</summary>
    Coaching,
    Warning,
}

/// <summary>A timestamped, scored moment during a flight (takeoff, a taxi overspeed, the touchdown).</summary>
public sealed record FlightEvent(DateTimeOffset At, FlightEventSeverity Severity, string Message);

/// <summary>
/// The raw result of tracking one flight, produced by <see cref="FlightTracker"/>. The economy
/// settles this into an itemised payout (Phase 1f); this record holds only what was observed.
/// </summary>
public sealed record FlightRecord(
    string AircraftTitle,
    DateTimeOffset DepartedAt,
    DateTimeOffset ArrivedAt,
    double TouchdownFpm,
    double MaxAltitudeFt,
    double DepartureLat,
    double DepartureLon,
    double ArrivalLat,
    double ArrivalLon,
    double DistanceNm,
    double FuelUsedLbs,
    IReadOnlyList<FlightEvent> Events)
{
    public TimeSpan BlockTime => ArrivedAt - DepartedAt;

    // ── Phase 7b scoring ──────────────────────────────────────────────────────────────────────────
    // The un-gameable landing grade and the stabilised-approach / enroute assessment, computed by the
    // tracker from the expanded telemetry. Set by FlightTracker.Complete(); every other construction
    // site (check-flights, tests) keeps the neutral defaults so this stays additive. Phase 7c is what
    // turns these into pay / XP / reputation / wear — here they are only computed, recorded, and shown.

    /// <summary>The most-negative of the last three airborne descent-rate samples (fpm) — can't be gamed
    /// by cherry-picking one soft frame. <see cref="TouchdownFpm"/> stays the raw last-frame rate.</summary>
    public double TouchdownFpmWorst3 { get; init; }

    /// <summary>Peak load factor around ground contact, g.</summary>
    public double TouchdownG { get; init; } = 1.0;

    /// <summary>Bank angle at touchdown, degrees (magnitude).</summary>
    public double TouchdownBankDeg { get; init; }

    /// <summary>Landing grade 0..100 — the worst of the fpm, g, and bank sub-scores.</summary>
    public int LandingScore { get; init; } = 100;

    /// <summary>Stabilised-approach grade 0..100 — fraction of below-gate samples flown within limits.</summary>
    public int ApproachScore { get; init; } = 100;

    /// <summary>Enroute grade 0..100 — 100 minus accumulated exceedance points.</summary>
    public int EnrouteScore { get; init; } = 100;

    /// <summary>Composite flight score 0..100 (0.55·landing + 0.30·approach + 0.15·enroute).</summary>
    public int OverallScore { get; init; } = 100;

    /// <summary>Passenger-comfort grade 0..100 (Phase 10c): the whole-flight ride smoothness — peak bank, the g
    /// envelope, the touchdown, and exceedances. 100 = a limousine ride. The economy pays a comfort tip on a
    /// passenger leg from this; it has no effect on a cargo leg. Defaults to 100 so an unscored record is neutral.</summary>
    public int ComfortScore { get; init; } = 100;

    /// <summary>True when the approach stayed within stabilised-approach limits below the gate.</summary>
    public bool StabilizedApproach { get; init; } = true;

    /// <summary>Total exceedance points logged during the flight (overspeed, over-bank, over-g, stall…).</summary>
    public int ViolationPoints { get; init; }

    /// <summary>True only when the tracker actually assessed this flight from telemetry. Settlement uses
    /// the score-based lever (Phase 7c) only for a scored leg; a manual/legacy record keeps the raw-fpm
    /// lever. This is what lets 7c change how pay works without rewriting unscored settlement paths.</summary>
    public bool Scored { get; init; }

    /// <summary>False when flight-integrity monitoring caught a cheat (slew, or time-acceleration near the
    /// ground). An invalid flight forfeits any performance bonus (Phase 7c anti-cheat).</summary>
    public bool ScoreValid { get; init; } = true;

    /// <summary>Engine-damage percentage points this leg ADDED, as the sim's own damage model reported it
    /// (monotonic max − baseline; 0 below a small deadband, and when the sim doesn't publish damage). The
    /// economy prices this into engine-condition wear at settlement (Phase 9e) — because it's the sim's own
    /// authoritative figure, folded monotonically, it can't be gamed. A brief exceedance the sim deems
    /// harmless leaves it at 0 (no cost); only genuine, sustained abuse accrues.</summary>
    public double EngineDamagePctAccrued { get; init; }
}
