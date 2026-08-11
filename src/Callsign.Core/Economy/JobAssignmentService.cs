using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Progression;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>Accepting a job: freezes its quote onto a <see cref="JobAssignment"/> and takes it off the board.</summary>
public sealed class JobAssignmentService
{
    private readonly CallsignDbContext _db;
    private readonly IClock _clock;

    public JobAssignmentService(CallsignDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<JobAssignment> AcceptAsync(Guid jobId, Guid accountId, Guid pilotId, CancellationToken ct = default)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw new InvalidOperationException($"Job {jobId} not found.");

        // Rank gate (Phase 3b): the board shows jobs above your rank locked with the reason; refuse them
        // here too, so the gate holds even if a stale board is submitted.
        var pilot = await _db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId, ct)
                    ?? throw new InvalidOperationException($"Pilot {pilotId} not found.");
        if (pilot.Rank < job.RequiredRank)
            throw new InvalidOperationException(
                $"{RankTiers.Def(job.RequiredRank).DisplayName} required — you're a {RankTiers.Def(pilot.Rank).DisplayName}.");

        var assignment = new JobAssignment
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AccountId = accountId,
            PilotId = pilotId,
            Type = job.Type,
            OriginIcao = job.OriginIcao,
            DestIcao = job.DestIcao,
            Commodity = job.Commodity,
            WeightLbs = job.WeightLbs,
            Pax = job.Pax,
            DistanceNm = job.DistanceNm,
            RewardQuoteCents = job.RewardCents, // FREEZE the quote
            XpQuote = job.Xp,
            Status = AssignmentStatus.Accepted,
            AcceptedAt = _clock.UtcNow,
        };

        _db.JobAssignments.Add(assignment);
        _db.Jobs.Remove(job); // taken off the board
        await _db.SaveChangesAsync(ct);
        return assignment;
    }
}
