using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A certificate's live standing for a company: whether it's held and valid, when it lapses, and —
/// if not yet held — whether the company clears the standards bar to apply.</summary>
public sealed record CertificateStatus(
    CertificateKind Kind, string DisplayName, string Blurb, string[] GatesLabels,
    bool Held, bool Valid, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, int? DaysLeft,
    long FeeCents, int ValidityDays, int MinReputationMilli, int MinCompletedFlights,
    int ReputationMilli, int CompletedFlights, long CashCents,
    bool MeetsReputation, bool MeetsRecord, bool CanAfford, bool CanApply, string? Blocker);

/// <summary>
/// Operating certificates (Phase 8e): the regulated licences that gate a category of higher-value work. A
/// company EARNS one by application — meeting a standards bar (reputation + a track record of settled
/// deliveries) and paying a fee — HOLDS it until it expires, and RENEWS it by applying again. This is the
/// mid-game progression gate: a real action and an ongoing cost, layered over the passive rank/reputation
/// thresholds. Authorisation is a pure check of a valid certificate against the job's mission type.
/// </summary>
public sealed class CertificateService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;

    public CertificateService(CallsignDbContext db, LedgerService ledger, IClock clock)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
    }

    /// <summary>The mission types a company is currently authorised to fly are those whose required certificate
    /// it holds valid — this returns the set of valid certificate kinds for the cheap per-job gate check.</summary>
    public async Task<HashSet<CertificateKind>> ValidKindsAsync(Guid companyId, DateTimeOffset now, CancellationToken ct = default)
        => (await _db.OperatingCertificates.Where(c => c.CompanyId == companyId && c.ExpiresAt > now)
                .Select(c => c.Kind).ToListAsync(ct)).ToHashSet();

    /// <summary>Whether a held valid-certificate set authorises a mission type (no required cert = always).</summary>
    public static bool Authorizes(HashSet<CertificateKind> validKinds, MissionType type)
    {
        var req = CertificateCatalog.RequiredFor(type);
        return req is null || validKinds.Contains(req.Value);
    }

    /// <summary>Every certificate's standing for a company — held ones with their expiry, and applyable ones
    /// with the standards bar and whether the company clears it.</summary>
    public async Task<IReadOnlyList<CertificateStatus>> GetStatusAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        int rep = await _db.Pilots.Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.ReputationMilli).Select(p => (int?)p.ReputationMilli).FirstOrDefaultAsync(ct) ?? 0;
        int flights = await _db.JobAssignments.CountAsync(a => a.AccountId == companyId && a.Status == AssignmentStatus.Settled, ct);
        long cash = company?.CashCents ?? 0;
        var held = await _db.OperatingCertificates.Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Kind, ct);

        var list = new List<CertificateStatus>(CertificateCatalog.All.Count);
        foreach (var def in CertificateCatalog.All)
        {
            held.TryGetValue(def.Kind, out var cert);
            bool valid = cert is not null && cert.IsValidAt(now);
            int? daysLeft = cert is not null ? Math.Max(0, (int)Math.Ceiling((cert.ExpiresAt - now).TotalDays)) : null;

            bool meetsRep = rep >= def.MinReputationMilli;
            bool meetsRecord = flights >= def.MinCompletedFlights;
            bool canAfford = cash >= def.FeeCents;
            bool canApply = meetsRep && meetsRecord && canAfford;
            string? blocker = !meetsRep ? $"Reputation {def.MinReputationMilli / 1000.0:0.0} required"
                : !meetsRecord ? $"{def.MinCompletedFlights} completed deliveries required"
                : !canAfford ? "Not enough cash for the fee"
                : null;

            var gatesLabels = def.GatesTypes.Select(t => MissionCatalog.TryDef(t)?.DisplayName ?? t.ToString()).ToArray();
            list.Add(new CertificateStatus(
                def.Kind, def.DisplayName, def.Blurb, gatesLabels,
                Held: cert is not null, Valid: valid, cert?.IssuedAt, cert?.ExpiresAt, daysLeft,
                def.FeeCents, def.ValidityDays, def.MinReputationMilli, def.MinCompletedFlights,
                rep, flights, cash, meetsRep, meetsRecord, canAfford, canApply, blocker));
        }
        return list;
    }

    /// <summary>
    /// Apply for (or renew) a certificate: enforce the standards bar, charge the fee via the ledger, and
    /// issue/extend the certificate. A renewal while still valid extends from the current expiry (no time
    /// lost); an expired or fresh one runs from now. Idempotent via <paramref name="idempotencyKey"/>.
    /// </summary>
    public async Task<OperatingCertificate> ApplyAsync(
        Guid companyId, CertificateKind kind, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var def = CertificateCatalog.Def(kind);
        // Namespace the keyed dedupe by kind (mirroring the no-key path) so one Idempotency-Key reused across
        // two different certificate kinds doesn't collide and mis-replay the wrong kind.
        string? dedupe = idempotencyKey is null ? null : $"cert-apply:{kind}:{idempotencyKey}";
        if (dedupe is not null && await _ledger.FindByDedupeKeyAsync(companyId, dedupe, ct) is not null)
            return await _db.OperatingCertificates.FirstAsync(c => c.CompanyId == companyId && c.Kind == kind, ct);

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var now = _clock.UtcNow;

        int rep = await _db.Pilots.Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.ReputationMilli).Select(p => (int?)p.ReputationMilli).FirstOrDefaultAsync(ct) ?? 0;
        if (rep < def.MinReputationMilli)
            throw new InvalidOperationException($"Reputation {def.MinReputationMilli / 1000.0:0.0} required — yours is {rep / 1000.0:0.0}.");
        int flights = await _db.JobAssignments.CountAsync(a => a.AccountId == companyId && a.Status == AssignmentStatus.Settled, ct);
        if (flights < def.MinCompletedFlights)
            throw new InvalidOperationException($"{def.MinCompletedFlights} completed deliveries required — you have {flights}.");
        if (company.CashCents < def.FeeCents)
            throw new InvalidOperationException($"Not enough cash: {def.DisplayName} costs {def.FeeCents / 100m:C0}, you have {company.Cash:C0}.");

        var cert = await _db.OperatingCertificates.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Kind == kind, ct);
        // Renew from the later of now and the current expiry, so renewing early never burns remaining validity.
        var basis = cert is not null && cert.IsValidAt(now) ? cert.ExpiresAt : now;
        var newExpiry = basis.AddDays(def.ValidityDays);
        if (cert is null)
        {
            cert = new OperatingCertificate { Id = Guid.NewGuid(), CompanyId = companyId, Kind = kind, IssuedAt = now, ExpiresAt = newExpiry, UpdatedAt = now };
            _db.OperatingCertificates.Add(cert);
        }
        else
        {
            cert.IssuedAt = now;
            cert.ExpiresAt = newExpiry;
            cert.UpdatedAt = now;
        }

        await _ledger.StageBatchAsync(companyId, new[]
        {
            // With no idempotency key each application is a distinct paid transaction — a fresh Guid keeps two
            // renewals in the same instant from colliding on the ledger's unique dedupe index.
            new LedgerPosting(LedgerCategory.CertificateFee, -(def.FeeCents / 100m), $"{def.DisplayName}",
                DedupeKey: dedupe ?? $"cert-apply:{companyId}:{kind}:{Guid.NewGuid()}"),
        }, ct);

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (dedupe is not null)
        {
            _db.ChangeTracker.Clear();
            return await _db.OperatingCertificates.FirstAsync(c => c.CompanyId == companyId && c.Kind == kind, ct);
        }
        return cert;
    }
}
