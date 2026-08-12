using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Achievements;

/// <summary>
/// Evaluates the <see cref="AchievementCatalog"/> against a company's current progress and awards any
/// newly-earned badges — idempotently (a unique (CompanyId, Key) index means a badge is granted once).
/// Evaluation is lazy: the read endpoint calls it, so badges catch up whenever the player looks, without
/// hooking every event. Returns the full roster (earned + locked, each with progress) for display.
/// </summary>
public sealed class AchievementService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;
    private readonly ProgressMetricsService _metrics;

    public AchievementService(CallsignDbContext db, IClock clock, ProgressMetricsService metrics)
    {
        _db = db;
        _clock = clock;
        _metrics = metrics;
    }

    public async Task<IReadOnlyList<AchievementView>> EvaluateAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);

        var earnedAt = await _db.AchievementAwards
            .Where(a => a.CompanyId == companyId && !a.IsDeleted)
            .ToDictionaryAsync(a => a.Key, a => a.EarnedAt, ct);

        var now = _clock.UtcNow;
        var awardedThisPass = false;
        foreach (var def in AchievementCatalog.All)
        {
            if (earnedAt.ContainsKey(def.Key) || !def.IsEarnedBy(m))
                continue;
            _db.AchievementAwards.Add(new AchievementAward
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                Key = def.Key,
                EarnedAt = now,
                UpdatedAt = now,
            });
            earnedAt[def.Key] = now;
            awardedThisPass = true;
        }
        if (awardedThisPass)
            await _db.SaveChangesAsync(ct);

        return AchievementCatalog.All.Select(def => new AchievementView(
            def.Key, def.Name, def.Description, def.Category, def.Target,
            Math.Min(def.ProgressOf(m), def.Target),
            earnedAt.ContainsKey(def.Key),
            earnedAt.TryGetValue(def.Key, out var at) ? at : null)).ToList();
    }
}
