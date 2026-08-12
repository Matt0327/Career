namespace Callsign.Core.Domain;

/// <summary>
/// An append-only entry in the pilot's reputation log (Phase 3f). Reputation drifts in tiny amounts per
/// delivery, so an itemised log keeps the movement legible instead of an opaque number. Local-only.
/// </summary>
public sealed class ReputationEvent
{
    public long Id { get; set; }              // autoincrement local order key
    public Guid PilotId { get; set; }
    public int DeltaMilli { get; set; }       // reputation change applied (thousandths; can be negative)
    public int BalanceMilli { get; set; }     // reputation after this event
    public string Reason { get; set; } = null!;
    public DateTimeOffset At { get; set; }
}
