namespace Callsign.Core.Domain;

/// <summary>What a policy covers. Pinned (persisted as a string).</summary>
public enum InsuranceScope { Aircraft = 1, Fleet = 2 }

/// <summary>
/// An insurance policy (Phase 4c): a POLICY + CLAIM path, not just a premium. A recurring premium (billed
/// in the reconcile pass) buys coverage — a fraction of an airframe's hull value, minus a deductible; a
/// covered total loss files a claim that pays out. Syncable for the future shared world.
/// </summary>
public sealed class InsurancePolicy : ISyncable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public InsuranceScope Scope { get; set; } = InsuranceScope.Aircraft;
    public Guid? AircraftInstanceId { get; set; }   // the insured airframe (Aircraft scope)
    public long HullValueCents { get; set; }        // agreed hull value at underwriting
    public int CoverageMilli { get; set; }          // fraction of hull value covered (0..100000)
    public long DeductibleCents { get; set; }       // what you pay on a claim
    public long PremiumPerWeekCents { get; set; }
    public DateTimeOffset PremiumLastBilledAt { get; set; } // reconcile watermark
    public bool Active { get; set; } = true;
    public DateTimeOffset StartedAt { get; set; }

    // Sync hooks (dormant until the Phase-4 shared-world ADR).
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public Guid? OriginClientId { get; set; }

    /// <summary>The covered value (hull × coverage) and the claim payout (covered − deductible).</summary>
    public long CoveredValueCents => (long)(HullValueCents * (CoverageMilli / 100_000.0));
    public long ClaimPayoutCents => System.Math.Max(0, CoveredValueCents - DeductibleCents);
}
