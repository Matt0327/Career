namespace Callsign.Core.Flight;

/// <summary>Severity of a scored flight event, for the live event log.</summary>
public enum FlightEventSeverity
{
    Info,
    Success,
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

    /// <summary>True when the approach stayed within stabilised-approach limits below the gate.</summary>
    public bool StabilizedApproach { get; init; } = true;

    /// <summary>Total exceedance points logged during the flight (overspeed, over-bank, over-g, stall…).</summary>
    public int ViolationPoints { get; init; }
}
