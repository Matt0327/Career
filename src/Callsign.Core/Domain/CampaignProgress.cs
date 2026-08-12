namespace Callsign.Core.Domain;

/// <summary>
/// A company's progress through one authored campaign (Phase 5b). The arc's steps live in code (a
/// self-documenting <c>CampaignCatalog</c>); this row tracks only <em>where</em> the company is:
/// <see cref="StepIndex"/> is the next step to complete (0-based), <see cref="StepStartedAt"/> is when it
/// became current, and <see cref="CompletedAt"/> is set once the whole arc is done (and its reward paid).
/// A unique (CompanyId, CampaignKey) index keeps one progress row per campaign. Syncable.
/// </summary>
public sealed class CampaignProgress : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string CampaignKey { get; set; } = null!;   // matches a CampaignDef.Key in the catalog
    public int StepIndex { get; set; }                  // next step to satisfy (0-based); == step count when done
    public DateTimeOffset StepStartedAt { get; set; }   // when the current step became active
    public DateTimeOffset? CompletedAt { get; set; }    // set when the arc completes and the reward is paid

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }
}
