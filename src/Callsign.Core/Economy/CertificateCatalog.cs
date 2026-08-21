using Callsign.Core.Domain;

namespace Callsign.Core.Economy;

/// <summary>
/// One operating certificate's requirements and economics (Phase 8e) — original, freely retunable. A
/// certificate authorises the mission types in <see cref="GatesTypes"/>; earning it costs <see cref="FeeCents"/>
/// and demands a standards bar: a minimum reputation AND a minimum track record (settled deliveries). It is
/// valid for <see cref="ValidityDays"/> and renews by re-applying (paying the fee again).
/// </summary>
public sealed record CertificateDef(
    CertificateKind Kind,
    string DisplayName,
    string Blurb,
    MissionType[] GatesTypes,
    long FeeCents,
    int ValidityDays,
    int MinReputationMilli,
    int MinCompletedFlights);

/// <summary>
/// The operating certificates a company can hold (Phase 8e). Charter unlocks premium passenger charter (VIP)
/// work; Hazmat unlocks dangerous-goods loads. Both sit in the same reputation band as the existing
/// Emergency/SAR gates, but add a real ACTION — an application with a fee and an expiry — on top of the
/// passive rank/reputation thresholds.
/// </summary>
public static class CertificateCatalog
{
    public static readonly IReadOnlyList<CertificateDef> All =
    [
        new(CertificateKind.Charter, "Air Charter Certificate",
            "Authorises commercial passenger charter — the executive and private-client (VIP) work.",
            [MissionType.Vip], FeeCents: 4_500_000, ValidityDays: 120, MinReputationMilli: 5_000, MinCompletedFlights: 15),
        new(CertificateKind.Hazmat, "Dangerous Goods Endorsement",
            "Authorises carriage of hazardous loads — chemicals, compressed gas, explosives.",
            [MissionType.Hazardous], FeeCents: 6_500_000, ValidityDays: 120, MinReputationMilli: 7_000, MinCompletedFlights: 25),
        // Phase 11f — the marquee endgame gate. GatesTypes is empty: it authorises a ROUTE CATEGORY (scheduled
        // passenger service), checked directly in RouteService, not a freelance job mission type.
        new(CertificateKind.AirOperator, "Air Operator Certificate",
            "Authorises a scheduled-passenger network — real seats, timetabled service, and load-factor economics.",
            Array.Empty<MissionType>(), FeeCents: 15_000_000, ValidityDays: 180, MinReputationMilli: 35_000, MinCompletedFlights: 60),
    ];

    public static CertificateDef Def(CertificateKind kind) => All.First(d => d.Kind == kind);

    /// <summary>The certificate a mission type demands, or null if it needs none (the open, ungated work).</summary>
    public static CertificateKind? RequiredFor(MissionType type)
    {
        foreach (var d in All)
            if (Array.IndexOf(d.GatesTypes, type) >= 0)
                return d.Kind;
        return null;
    }
}
