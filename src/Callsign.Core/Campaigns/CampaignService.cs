using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Campaigns;

public sealed record CampaignStepView(string Title, string Detail, long Target, long Progress, bool Done);

public sealed record CampaignView(
    string Key, string Name, string Description, long RewardCents,
    int StepIndex, int StepCount, bool Completed, DateTimeOffset? CompletedAt, IReadOnlyList<CampaignStepView> Steps);

/// <summary>
/// Advances a company through the <see cref="CampaignCatalog"/>. Evaluated lazily on read: the current
/// step of each arc is tested against the shared progress snapshot; satisfied steps advance in order (a
/// single evaluation can clear several), and clearing the last step marks the arc complete and pays its
/// reward through the ledger — once (guarded by <c>CompletedAt</c> plus a dedupe key). Returns every arc
/// with its step checklist for display.
/// </summary>
public sealed class CampaignService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly ProgressMetricsService _metrics;
    private readonly LedgerService _ledger;

    public CampaignService(CallsignDbContext db, IClock clock, ProgressMetricsService metrics, LedgerService ledger)
    {
        _db = db;
        _clock = clock;
        _metrics = metrics;
        _ledger = ledger;
    }

    public async Task<IReadOnlyList<CampaignView>> EvaluateAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);
        var now = _clock.UtcNow;
        var byKey = (await _db.CampaignProgress.Where(c => c.CompanyId == companyId && !c.IsDeleted).ToListAsync(ct))
            .ToDictionary(c => c.CampaignKey);

        var pendingSave = false;
        foreach (var def in CampaignCatalog.All)
        {
            if (!byKey.TryGetValue(def.Key, out var progress))
            {
                progress = new CampaignProgress
                {
                    Id = Guid.NewGuid(),
                    CompanyId = companyId,
                    CampaignKey = def.Key,
                    StepIndex = 0,
                    StepStartedAt = now,
                    UpdatedAt = now,
                };
                _db.CampaignProgress.Add(progress);
                byKey[def.Key] = progress;
                pendingSave = true;
            }

            // Clear any already-satisfied steps in order; clearing the last one completes the arc.
            while (progress.CompletedAt is null
                   && progress.StepIndex < def.Steps.Count
                   && def.Steps[progress.StepIndex].IsMetBy(m))
            {
                progress.StepIndex++;
                progress.StepStartedAt = now;
                progress.UpdatedAt = now;
                pendingSave = true;

                if (progress.StepIndex >= def.Steps.Count)
                {
                    progress.CompletedAt = now;
                    if (def.RewardCents > 0)
                    {
                        // PostAsync commits the whole unit of work (the reward row + cash + the campaign
                        // mutations above) in one transaction; the dedupe key makes the payout idempotent.
                        await _ledger.PostAsync(companyId, LedgerCategory.CampaignReward, def.RewardCents / 100m,
                            $"Campaign reward — {def.Name}", LedgerRefType.Campaign, def.Key,
                            dedupeKey: $"campaign:{def.Key}:reward", ct: ct);
                        pendingSave = false; // already saved by PostAsync
                    }
                }
            }
        }

        if (pendingSave)
            await _db.SaveChangesAsync(ct);

        return CampaignCatalog.All.Select(def => ToView(def, byKey[def.Key], m)).ToList();
    }

    private static CampaignView ToView(CampaignDef def, CampaignProgress p, ProgressMetrics m)
    {
        var steps = def.Steps
            .Select((s, i) => new CampaignStepView(s.Title, s.Detail, s.Target, Math.Min(s.ProgressOf(m), s.Target), i < p.StepIndex))
            .ToList();
        return new CampaignView(def.Key, def.Name, def.Description, def.RewardCents,
            p.StepIndex, def.Steps.Count, p.CompletedAt is not null, p.CompletedAt, steps);
    }
}
