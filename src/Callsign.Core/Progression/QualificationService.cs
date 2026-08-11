using Callsign.Core.Data;
using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Progression;

/// <summary>Reads (Phase 3c) and, later, awards (Phase 3d) a pilot's licence classes.</summary>
public sealed class QualificationService
{
    private readonly CallsignDbContext _db;

    public QualificationService(CallsignDbContext db) => _db = db;

    public Task<List<PilotQualification>> GetHeldAsync(Guid pilotId, CancellationToken ct = default)
        => _db.PilotQualifications.Where(q => q.PilotId == pilotId && !q.IsDeleted).ToListAsync(ct);

    public async Task<HashSet<QualClass>> HeldClassesAsync(Guid pilotId, CancellationToken ct = default)
        => (await _db.PilotQualifications.Where(q => q.PilotId == pilotId && !q.IsDeleted)
                .Select(q => q.Class).ToListAsync(ct)).ToHashSet();

    /// <summary>Does the pilot hold the class needed to fly this category?</summary>
    public Task<bool> IsRatedAsync(Guid pilotId, QualClass required, CancellationToken ct = default)
        => _db.PilotQualifications.AnyAsync(q => q.PilotId == pilotId && q.Class == required && !q.IsDeleted, ct);
}
