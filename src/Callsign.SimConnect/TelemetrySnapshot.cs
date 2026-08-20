namespace Callsign.SimConnect;

/// <summary>
/// One sampled frame of simulator telemetry. An immutable snapshot the rest of the app consumes
/// without caring whether it came from SimConnect or the fake source.
/// </summary>
public sealed record TelemetrySnapshot
{
    /// <summary>Monotonic sample number since the connection opened.</summary>
    public required long Sequence { get; init; }

    /// <summary>When this sample was taken (wall clock).</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>Indicated altitude, feet.</summary>
    public required double AltitudeFt { get; init; }

    /// <summary>Indicated airspeed, knots.</summary>
    public required double IndicatedAirspeedKts { get; init; }

    /// <summary>Ground speed, knots.</summary>
    public required double GroundSpeedKts { get; init; }

    /// <summary>Vertical speed, feet per minute (negative = descending).</summary>
    public required double VerticalSpeedFpm { get; init; }

    /// <summary>Latitude, decimal degrees.</summary>
    public required double LatitudeDeg { get; init; }

    /// <summary>Longitude, decimal degrees.</summary>
    public required double LongitudeDeg { get; init; }

    /// <summary>Total fuel on board, pounds.</summary>
    public required double FuelQuantityLbs { get; init; }

    /// <summary>True when the sim reports the aircraft is on the ground.</summary>
    public required bool OnGround { get; init; }

    /// <summary>Raw aircraft TITLE string as reported by the sim (livery-dependent).</summary>
    public required string AircraftTitle { get; init; }

    // ── Phase 7b substrate ────────────────────────────────────────────────────────────────────────
    // Added so the tracker can judge a landing and an approach the way real flight-data monitoring does.
    // All default to neutral values so callers and tests that predate 7b construct a snapshot unchanged;
    // the live SimConnect source and the synthetic source fill them in.

    /// <summary>Load factor, g (1.0 = level, unaccelerated flight).</summary>
    public double GForce { get; init; } = 1.0;

    /// <summary>Pitch attitude, degrees (positive = nose up).</summary>
    public double PitchDeg { get; init; }

    /// <summary>Bank angle, degrees (sign is sim-dependent; scoring uses the magnitude).</summary>
    public double BankDeg { get; init; }

    /// <summary>True heading, degrees.</summary>
    public double HeadingDegTrue { get; init; }

    /// <summary>Height above ground, feet. 0 = not reported; the tracker falls back to indicated altitude.</summary>
    public double AltitudeAglFt { get; init; }

    /// <summary>Flap handle position, percent (0 = clean).</summary>
    public double FlapsPercent { get; init; }

    /// <summary>Landing-gear extension, percent (100 = down and locked; fixed-gear reports 100).</summary>
    public double GearPercent { get; init; } = 100;

    /// <summary>True while the sim is annunciating a stall warning.</summary>
    public bool StallWarning { get; init; }

    /// <summary>True while the sim is annunciating an overspeed warning.</summary>
    public bool OverspeedWarning { get; init; }

    /// <summary>Simulation rate multiplier (1.0 = real time). Recorded for the Phase 7c anti-cheat pass.</summary>
    public double SimRate { get; init; } = 1.0;

    /// <summary>True while slew mode is active (a teleport/cheat tell). Recorded for the Phase 7c pass.</summary>
    public bool SlewActive { get; init; }

    // --- Ambient weather (Phase 8): the sim's actual conditions, for scoring a flown leg's difficulty (law L7) ---
    /// <summary>Sustained ambient wind speed (kt). Default 0 = calm, so no-weather telemetry adds no difficulty.</summary>
    public double AmbientWindKts { get; init; }
    /// <summary>Wind direction (degrees, FROM). Combined with heading at touchdown to get the crosswind component.</summary>
    public double AmbientWindDirDeg { get; init; }
    /// <summary>Surface visibility (statute miles). Default 10 = clear, so no-weather telemetry adds no difficulty.</summary>
    public double AmbientVisibilitySm { get; init; } = 10;

    // --- Authoritative touchdown (Phase 9c): the sim captures the exact touchdown state at contact, so a low
    // sample rate can't miss the peak. Default 0 = not reported → the tracker keeps its sampled worst-of-three
    // (L10: a missing signal changes nothing). The sim value only ever makes the grade harder, never softer.
    /// <summary>Vertical velocity AT the moment of touchdown (feet per second, magnitude used). ×60 = touchdown fpm.</summary>
    public double TouchdownNormalVelocityFps { get; init; }
    /// <summary>Lateral (side-load) velocity at touchdown (feet per second) — a firm crab-on-touchdown de-crab tell.</summary>
    public double TouchdownLateralVelocityFps { get; init; }

    // --- Weight & balance (Phase 9d): checked once at takeoff. All default 0 = not reported → no W&B check runs
    // (L10). Comparisons are relative (weight vs its own MTOW, CG vs its own envelope), so the scoring is
    // correct regardless of the sim's percent scale.
    /// <summary>Current gross weight, pounds.</summary>
    public double TotalWeightLbs { get; init; }
    /// <summary>Maximum gross (takeoff) weight, pounds — the MTOW limit to compare against.</summary>
    public double MaxGrossWeightLbs { get; init; }
    /// <summary>Longitudinal centre of gravity (percent MAC, sim scale).</summary>
    public double CgPercent { get; init; }
    /// <summary>Forward CG limit (same scale as <see cref="CgPercent"/>). 0 = not reported.</summary>
    public double CgFwdLimit { get; init; }
    /// <summary>Aft CG limit (same scale as <see cref="CgPercent"/>). 0 = not reported.</summary>
    public double CgAftLimit { get; init; }

    // --- Engine damage (Phase 9e): the sim's OWN cumulative engine-damage figure (GENERAL ENG DAMAGE PERCENT),
    // 0 = pristine. The tracker baselines it at first sight and accrues the leg's increase, which is priced into
    // engine wear at settlement. Default 0 → an aircraft that doesn't publish it accrues no wear (L10). Read,
    // never commanded (L7); folded monotonically, so it can only ever add authoritative wear, never soften it.
    /// <summary>Cumulative engine damage as the simulator models it, percent (0 = pristine).</summary>
    public double EngineDamagePercent { get; init; }
}
