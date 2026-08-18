using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>
/// A completed, settled flight (brief §6). The payout is itemised in
/// <see cref="PayoutBreakdownJson"/>, and every line of that breakdown maps 1:1 to a ledger row so
/// any figure is traceable to the ledger (acceptance #3).
/// </summary>
public sealed class Flight
{
    public Guid Id { get; set; }
    public Guid? JobAssignmentId { get; set; }
    public Guid FlownByPilotId { get; set; }
    public string AircraftTitle { get; set; } = null!;
    public Guid? AircraftTypeId { get; set; }
    public Guid? AircraftInstanceId { get; set; }  // the owned airframe flown, when the leg was flown in one (P2b)
    public DateTimeOffset DepartedAt { get; set; }
    public DateTimeOffset ArrivedAt { get; set; }
    public double TouchdownFpm { get; set; }
    public double DistanceNm { get; set; }
    public double FuelUsedLbs { get; set; }
    public long PayoutCents { get; set; }
    public int Xp { get; set; }
    public string PayoutBreakdownJson { get; set; } = null!;
    public DateTimeOffset SettledAt { get; set; }

    // Phase 7b — the scored assessment of how the leg was flown. Nullable: flights recorded before 7b
    // have no scores and read as "not scored". Phase 7c is what turns these into pay / XP / rep / wear.
    public double? TouchdownFpmWorst3 { get; set; }
    public double? TouchdownG { get; set; }
    public int? LandingScore { get; set; }
    public int? ApproachScore { get; set; }
    public int? OverallScore { get; set; }
    public bool? StabilizedApproach { get; set; }
    public int? ViolationPoints { get; set; }
    public bool? ScoreValid { get; set; }   // Phase 7c — false when flight-integrity monitoring voided the score

    [NotMapped]
    public decimal Payout => PayoutCents / 100m;
}
