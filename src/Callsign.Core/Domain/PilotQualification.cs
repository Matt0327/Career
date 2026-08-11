namespace Callsign.Core.Domain;

/// <summary>
/// A pilot licence class (Phase 3c) — the rating an aircraft category demands. Pinned letters
/// (persisted as a string). H = helicopter, M = glider; the rest ladder up by aircraft complexity.
/// </summary>
public enum QualClass { A, B, C, D, E, F, H, M }

/// <summary>
/// A licence class a pilot holds. Holding the class rates you to fly aircraft of the matching category.
/// <see cref="Stars"/> and <see cref="BestTouchdownFpm"/> are earned and refined by check-flights
/// (Phase 3d). Syncable for the future shared world.
/// </summary>
public sealed class PilotQualification : ISyncable
{
    public Guid Id { get; set; }
    public Guid PilotId { get; set; }
    public QualClass Class { get; set; }
    public int Stars { get; set; }                 // 0..5, refined by check-flights (3d)
    public double? BestTouchdownFpm { get; set; }
    public Guid? CheckFlightId { get; set; }        // the attempt that earned it (3d)
    public DateTimeOffset EarnedAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
