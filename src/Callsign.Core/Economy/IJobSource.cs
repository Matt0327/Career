using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>A candidate destination for job generation.</summary>
public sealed record JobCandidate(string DestIcao, double DistanceNm);

/// <summary>Inputs for a batch of generated jobs.</summary>
public sealed record JobGenerationRequest(
    string OriginIcao,
    IReadOnlyList<JobCandidate> Candidates,
    PilotRank PilotRank,
    int Count,
    int Seed);

/// <summary>A generated job offer (not yet persisted).</summary>
public sealed record GeneratedJob(
    MissionType Type,
    string OriginIcao,
    string DestIcao,
    string Commodity,
    int WeightLbs,
    int Pax,
    double DistanceNm,
    long RewardCents,
    int Xp,
    PilotRank RequiredRank);

/// <summary>
/// The seam that produces job offers. Kept an interface from day one so a future shared-world server
/// could supply jobs wholesale without the rest of the app assuming they are generated locally
/// (addendum §6 / domain-notes).
/// </summary>
public interface IJobSource
{
    IReadOnlyList<GeneratedJob> Generate(JobGenerationRequest request);
}
