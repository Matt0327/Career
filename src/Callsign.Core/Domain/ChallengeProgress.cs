namespace Callsign.Core.Domain;

/// <summary>
/// A company's state for one rotating challenge in one period (Phase 12). The challenge itself lives in code
/// (the <c>ChallengeCatalog</c>); this row records only what the period-DELTA needs: <see cref="Baseline"/> is
/// the metric value captured the first time the challenge was seen this period, so progress is
/// <c>current − baseline</c>, and <see cref="ClaimedAt"/> marks the reward paid (once). <see cref="PeriodKey"/>
/// is the day/week bucket ("D:2026-08-23", "W:2026-34"); a new period starts fresh rows with fresh baselines.
/// A unique (CompanyId, PeriodKey, ChallengeKey) index keeps one row per challenge per period. Syncable.
/// </summary>
public sealed class ChallengeProgress : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string PeriodKey { get; set; } = null!;      // the day/week bucket, e.g. "D:2026-08-23" or "W:2026-34"
    public string ChallengeKey { get; set; } = null!;   // matches a ChallengeDef.Key in the catalog
    public long Baseline { get; set; }                   // the metric value when this period's challenge began
    public DateTimeOffset? ClaimedAt { get; set; }       // set when the reward is paid (once)

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
