namespace Callsign.Core.Domain;

/// <summary>Which side of the operation moved the airline's name (Phase 11a). Pinned because it is
/// persisted — reordering must never change the meaning of a stored row.</summary>
public enum AirlineRepSource
{
    /// <summary>A leg you flew yourself — the name moved by your telemetry score.</summary>
    Player = 0,
    /// <summary>An autonomous scheduled leg — the name eased toward the competence of the crew you chose.</summary>
    Crew = 1,
}

/// <summary>
/// An append-only entry in the airline's OPERATING reputation log (Phase 11a) — the company-scoped mirror of
/// <see cref="ReputationEvent"/> (which logs the PILOT's personal reputation). Kept separate on purpose: the
/// airline's name is earned by the operation you run (your flying + your crew's flying), never fed into the
/// pilot's SAR/Emergency job gate. The <see cref="Source"/> tag lets the UI show a two-source breakdown
/// ("from your flying / from your crew"). Local-only, like <see cref="ReputationEvent"/>.
/// </summary>
public sealed class AirlineReputationEvent
{
    public long Id { get; set; }              // autoincrement local order key
    public Guid CompanyId { get; set; }
    public int DeltaMilli { get; set; }       // operating-reputation change applied (thousandths; can be negative)
    public int BalanceMilli { get; set; }     // operating reputation after this event
    public AirlineRepSource Source { get; set; } = AirlineRepSource.Player;
    public string Reason { get; set; } = null!;
    public DateTimeOffset At { get; set; }
}
