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
}
