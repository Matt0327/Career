namespace Callsign.Core.Domain;

/// <summary>A category of regulated work that a company must be certified to fly (Phase 8e).</summary>
public enum CertificateKind
{
    /// <summary>Air charter certificate — required for premium passenger charter (VIP) work.</summary>
    Charter = 1,
    /// <summary>Dangerous-goods endorsement — required to carry hazardous loads.</summary>
    Hazmat = 2,
    /// <summary>Air operator certificate (Phase 11f) — required to run a scheduled-passenger network.</summary>
    AirOperator = 3,
}

/// <summary>
/// An operating certificate a company holds (Phase 8e): a regulated licence earned by application (a fee
/// plus a standards bar — reputation and a track record) that authorises a CATEGORY of higher-value work,
/// and that expires and must be renewed. The mid-game progression gate — a real step up, not just bigger
/// numbers. Persisted per company; one row per (company, kind), renewed in place.
/// </summary>
public sealed class OperatingCertificate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public CertificateKind Kind { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>When the certificate lapses. Authorisation is a pure check of this against the clock —
    /// valid while <see cref="ExpiresAt"/> is in the future.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsValidAt(DateTimeOffset now) => ExpiresAt > now;
}
