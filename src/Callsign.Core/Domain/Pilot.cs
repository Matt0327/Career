namespace Callsign.Core.Domain;

/// <summary>
/// Broad rank tiers (brief §2.1). Values are pinned because the enum is persisted —
/// reordering must never change the meaning of a stored row.
/// </summary>
public enum PilotRank
{
    Trainee = 0,
    Copilot = 1,
    Captain = 2,
    SeniorCaptain = 3,
    Chief = 4,
}

/// <summary>
/// The player's pilot: identity and progression only. Money lives on the owning
/// <see cref="Company"/>, not here — staff pilots (added in Phase 3) have no wallet.
/// </summary>
public sealed class Pilot : ISyncable
{
    public Guid Id { get; set; }

    /// <summary>Owning account (<see cref="Company"/>).</summary>
    public Guid CompanyId { get; set; }

    public string Name { get; set; } = null!;
    public PilotRank Rank { get; set; } = PilotRank.Trainee;
    public int Xp { get; set; }
    public string HomeIcao { get; set; } = null!;
    public string CurrentIcao { get; set; } = null!;

    /// <summary>Reputation in thousandths (0–100000 = 0.0–100.0) so tiny per-flight gains don't round away.</summary>
    public int ReputationMilli { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
