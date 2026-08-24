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
    private readonly EconomyConfig _cfg;

    public JobAssignmentService(CallsignDbContext db, IClock clock, EconomyConfig? cfg = null)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg ?? EconomyConfig.Default;
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
        var def = MissionCatalog.Def(job.Type);
        if (pilot.ReputationMilli < def.MinReputationMilli)
            throw new InvalidOperationException(
                $"Reputation {def.MinReputationMilli / 1000.0:0.0} required — yours is {pilot.ReputationMilli / 1000.0:0.0}.");

        var now = _clock.UtcNow;

        // Operating certificate gate (Phase 8e): premium categories (VIP charter, hazmat) demand a valid
        // company certificate. Enforced here too — not just on the board — so a stale offer can't slip through.
        if (CertificateCatalog.RequiredFor(job.Type) is { } reqCert
            && !await _db.OperatingCertificates.AnyAsync(c => c.CompanyId == accountId && c.Kind == reqCert && c.ExpiresAt > now, ct))
            throw new InvalidOperationException(
                $"{CertificateCatalog.Def(reqCert).DisplayName} required — apply in the Airline tab.");
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
            ClientKey = job.ClientKey,   // FREEZE the client (Phase 8d) — settlement credits this relationship
            ClientName = job.ClientName,
            Status = AssignmentStatus.Accepted,
            AcceptedAt = now,
            DeadlineAt = _cfg.MissionDeadline(job.Type, job.DistanceNm, now), // FREEZE the clock (Express/Emergency)
        };

        _db.JobAssignments.Add(assignment);
        _db.Jobs.Remove(job); // taken off the board
        await _db.SaveChangesAsync(ct);
        return assignment;
    }

    /// <summary>
    /// Cancel an accepted job the player hasn't flown yet. The assignment is marked Abandoned (freeing the
    /// pilot to take other work) and the client takes a TINY loyalty nick — you handed the job back rather
    /// than botching it, so it's far gentler than a failed delivery. Returns the client's name (if any) and
    /// the loyalty lost, for the confirmation message. Idempotent-ish: cancelling an already-settled or
    /// already-abandoned assignment throws.
    /// </summary>
    public async Task<(string? ClientName, int LoyaltyLostMilli)> CancelAsync(Guid assignmentId, Guid accountId, CancellationToken ct = default)
    {
        var a = await _db.JobAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.AccountId == accountId, ct)
                ?? throw new InvalidOperationException("Job not found.");
        if (a.Status != AssignmentStatus.Accepted)
            throw new InvalidOperationException("That job can no longer be cancelled.");

        a.Status = AssignmentStatus.Abandoned;

        int lost = 0;
        if (a.ClientKey is { } clientKey)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.CompanyId == accountId && c.ClientKey == clientKey, ct);
            if (client is not null)
            {
                int before = client.LoyaltyMilli;
                client.LoyaltyMilli = Math.Clamp(before + _cfg.ClientLoyaltyCancelMilli, 0, EconomyConfig.LoyaltyMax);
                client.UpdatedAt = _clock.UtcNow;
                lost = before - client.LoyaltyMilli;
            }
        }
        await _db.SaveChangesAsync(ct);
        return (a.ClientName, lost);
    }
}
