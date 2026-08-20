namespace Callsign.Core.Domain;

/// <summary>
/// A named client your company works for (Phase 8d) — the demand-side mirror of a hired crew member.
/// Jobs stop being anonymous: each board offer comes from a client, and completing it well builds that
/// client's <see cref="LoyaltyMilli"/> while a failure sours it. A loyal client pays a repeat premium
/// (and, later, sends exclusive offers); a burned one cools off.
///
/// PERSISTED — this is the Phase-8 exception to law L6 (the world is otherwise a pure function of place
/// and time). Loyalty is a genuine relationship state accumulated across the career, so it lives in a
/// table, keyed per company by a stable <see cref="ClientKey"/> (e.g. "EHRD#2") that the deterministic
/// roster regenerates identically each time the board refreshes.
/// </summary>
public sealed class Client
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Stable identity within a company — "{homeIcao}#{slot}" — so the roster maps back to the
    /// same row on every refresh. Unique per (company, key).</summary>
    public string ClientKey { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>The field this client operates out of — where their offers originate.</summary>
    public string HomeIcao { get; set; } = null!;

    /// <summary>Relationship strength in thousandths, clamped [0, 100000]. Rises with clean deliveries,
    /// drops with partials and failures.</summary>
    public int LoyaltyMilli { get; set; }

    public int JobsCompleted { get; set; }
    public int JobsFailed { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? LastJobAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
