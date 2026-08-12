namespace Callsign.Core.Domain;

/// <summary>
/// A milestone the company has earned (Phase 5a). The definitions live in code (a self-documenting
/// <c>AchievementCatalog</c>); this row just records that a given <see cref="Key"/> was reached, once,
/// with the moment it happened. A unique (CompanyId, Key) index makes awarding idempotent. Syncable, so
/// earned badges could travel to a future shared world.
/// </summary>
public sealed class AchievementAward : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Key { get; set; } = null!;   // matches an AchievementDef.Key in the catalog
    public DateTimeOffset EarnedAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
