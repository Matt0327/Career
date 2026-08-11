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
}
