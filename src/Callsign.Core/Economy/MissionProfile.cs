using Callsign.Core.Domain;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Economy;

/// <summary>How well the job itself was completed — distinct from how well the aircraft was flown.</summary>
public enum MissionGrade { Full = 1, Partial = 2, Failed = 3 }

/// <summary>
/// The result of judging a delivery against its mission type's own completion rules (Phase 7d). This is
/// what makes the mission TYPE matter: the same clean flight settles differently depending on what it was
/// carrying. <see cref="QualityMilli"/> (0..100000) scales the base reward; a Failed grade pays no base.
/// </summary>
public sealed record MissionOutcome(MissionGrade Grade, int QualityMilli, string? Reason);

/// <summary>
/// Per-type completion rules, evaluated from the recorded flight envelope at settlement. Cargo, passenger,
/// express and the rest keep a lax baseline (always Full) so their economics are unchanged; the premium
/// types earn real, distinct ways to win, lose, and get paid:
///   • Sensitive / Hazardous — FRAGILE freight: a firm arrival damages the goods; a slam destroys them.
///   • VIP / Tourist — RIDE QUALITY: a rough, exceedance-laden flight means unhappy passengers.
/// Everything here is computed from what 7b/7c already record — no new telemetry. Reference-speed and
/// per-sample-comfort refinements, plus the Express/Emergency delivery clock and Survey altitude band,
/// are deliberately left for later 7d slices.
/// </summary>
public static class MissionProfiles
{
    public static readonly MissionOutcome FullMark = new(MissionGrade.Full, 100_000, null);

    public static MissionOutcome Evaluate(MissionType type, FlightRecord f) => type switch
    {
        // Fragile freight — the touchdown (and any rough handling) is what damages the load.
        MissionType.Sensitive => Fragile(f, softFpm: 200, failFloorMilli: 55_000, mishandling: "cargo"),
        MissionType.Hazardous => Fragile(f, softFpm: 150, failFloorMilli: 60_000, mishandling: "hazardous load"),
        // Passenger ride quality — smoothness and no exceedances keep the client happy.
        MissionType.Vip     => Comfort(f, canFail: true,  failFloorMilli: 50_000, who: "The client"),
        MissionType.Tourist => Comfort(f, canFail: false, failFloorMilli: 0,      who: "The tour group"),
        // Everything else keeps the lax baseline — unchanged economics.
        _ => FullMark,
    };

    private static MissionOutcome Fragile(FlightRecord f, double softFpm, int failFloorMilli, string mishandling)
    {
        double sink = Math.Abs(f.Scored ? f.TouchdownFpmWorst3 : f.TouchdownFpm);
        double g = f.TouchdownG;
        int violations = f.ViolationPoints;

        double damage = 8_000 * Math.Max(0, sink - softFpm) / 100.0  // per 100 fpm over the soft floor
                      + 40_000 * Math.Max(0, g - 1.3)                 // per g over 1.3 at touchdown
                      + 2_000 * violations;                           // rough handling enroute
        int quality = (int)Math.Clamp(100_000 - damage, 0, 100_000);

        if (quality < failFloorMilli)
            return new(MissionGrade.Failed, 0, $"{mishandling} destroyed on a rough arrival");
        if (quality < 100_000)
            return new(MissionGrade.Partial, quality, $"{mishandling} damaged in transit");
        return FullMark;
    }

    private static MissionOutcome Comfort(FlightRecord f, bool canFail, int failFloorMilli, string who)
    {
        double g = f.TouchdownG;
        double bank = f.TouchdownBankDeg;
        int violations = f.ViolationPoints;

        double discomfort = 30_000 * Math.Max(0, g - 1.2)     // a firm touchdown they feel
                          + 3_000 * Math.Max(0, bank - 8)     // steep bank on landing
                          + 2_500 * violations;               // every exceedance rattles them
        // Tourist never fails — the ride is merely graded; VIP can lose the client outright.
        int quality = (int)Math.Clamp(100_000 - discomfort, canFail ? 0 : 55_000, 100_000);

        if (canFail && quality < failFloorMilli)
            return new(MissionGrade.Failed, 0, $"{who} refused to pay after a rough flight");
        if (quality < 100_000)
            return new(MissionGrade.Partial, quality, $"{who} was unimpressed with the ride");
        return FullMark;
    }
}
