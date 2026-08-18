using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;
using FlightRecord = Callsign.Core.Flight.FlightRecord;

namespace Callsign.Core.Tests;

public class MissionProfilesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // A tracker-graded record with the touchdown envelope the completion rules read.
    private static FlightRecord Landed(double worst3Fpm, double g = 1.0, double bank = 0, int violations = 0)
        => new("Cessna 172", T0, T0.AddMinutes(30), worst3Fpm, 5000, 0, 0, 0, 0, 100, 50, [])
        {
            Scored = true,
            TouchdownFpmWorst3 = worst3Fpm,
            TouchdownG = g,
            TouchdownBankDeg = bank,
            ViolationPoints = violations,
        };

    [Theory]
    [InlineData(MissionType.Cargo)]
    [InlineData(MissionType.Passenger)]
    [InlineData(MissionType.Express)]
    [InlineData(MissionType.Illicit)]
    public void OrdinaryJobs_AlwaysGradeFull_EvenOnASlam(MissionType type)
    {
        // The lax baseline: types without a completion rule are unchanged, however roughly they arrive.
        var o = MissionProfiles.Evaluate(type, Landed(-1200, g: 3.0, violations: 3));
        Assert.Equal(MissionGrade.Full, o.Grade);
        Assert.Equal(100_000, o.QualityMilli);
    }

    [Fact]
    public void FragileCargo_Greaser_IsFull()
        => Assert.Equal(MissionGrade.Full, MissionProfiles.Evaluate(MissionType.Sensitive, Landed(-80, g: 1.0)).Grade);

    [Fact]
    public void FragileCargo_FirmArrival_IsPartial_AndScalesPay()
    {
        var o = MissionProfiles.Evaluate(MissionType.Sensitive, Landed(-450, g: 1.2));
        Assert.Equal(MissionGrade.Partial, o.Grade);
        Assert.Equal(80_000, o.QualityMilli); // 8000 * (450-200)/100 = 20000 damage -> 80% intact
        Assert.NotNull(o.Reason);
    }

    [Fact]
    public void FragileCargo_Slam_IsDestroyed()
        => Assert.Equal(MissionGrade.Failed, MissionProfiles.Evaluate(MissionType.Sensitive, Landed(-1000, g: 1.5)).Grade);

    [Fact]
    public void Vip_SmoothRide_IsFull_ButARoughOne_Disappoints()
    {
        Assert.Equal(MissionGrade.Full, MissionProfiles.Evaluate(MissionType.Vip, Landed(-60, g: 1.1, bank: 2)).Grade);
        Assert.Equal(MissionGrade.Partial, MissionProfiles.Evaluate(MissionType.Vip, Landed(-200, g: 1.8)).Grade);
    }

    [Fact]
    public void Vip_ViolentFlight_LosesTheClient()
        => Assert.Equal(MissionGrade.Failed, MissionProfiles.Evaluate(MissionType.Vip, Landed(-300, g: 2.5, bank: 30, violations: 2)).Grade);

    // Express/Emergency carry a frozen deadline; the record's ArrivedAt (Landed uses T0 + 30 min) is
    // compared against it. On time is Full, late shaves the fee, way late is a failed delivery.
    [Fact]
    public void Express_OnTime_IsFull()
        => Assert.Equal(MissionGrade.Full, MissionProfiles.Evaluate(MissionType.Express, Landed(-80), deadlineAt: T0.AddMinutes(30)).Grade);

    [Fact]
    public void Express_Late_IsPartial_AndShavesTheFee()
    {
        var o = MissionProfiles.Evaluate(MissionType.Express, Landed(-80), deadlineAt: T0.AddMinutes(15)); // 15 min late
        Assert.Equal(MissionGrade.Partial, o.Grade);
        Assert.Equal(70_000, o.QualityMilli); // 2000/min × 15 = 30000 off
    }

    [Fact]
    public void Express_WayLate_MissesTheDeadline()
        => Assert.Equal(MissionGrade.Failed, MissionProfiles.Evaluate(MissionType.Express, Landed(-80), deadlineAt: T0).Grade); // 30 min late

    [Fact]
    public void Clock_WithNoFrozenDeadline_IsFull()
        => Assert.Equal(MissionGrade.Full, MissionProfiles.Evaluate(MissionType.Express, Landed(-80)).Grade);

    [Fact]
    public void Vip_UnscoredHardLanding_StillGradesDown()
    {
        // A manual/legacy record (Scored=false, so g/bank/violations are neutral defaults) carries only a
        // raw touchdown rate. A hard VIP arrival must still grade down, not sail through as Full.
        var rec = new FlightRecord("x", T0, T0.AddMinutes(30), -900, 5000, 0, 0, 0, 0, 100, 50, []);
        Assert.False(rec.Scored);
        Assert.NotEqual(MissionGrade.Full, MissionProfiles.Evaluate(MissionType.Vip, rec).Grade);
    }

    [Fact]
    public void Tourist_NeverFails_ButIsGraded()
    {
        // A rough tour is merely graded down (never a hard fail), floored so it always pays something.
        var o = MissionProfiles.Evaluate(MissionType.Tourist, Landed(-400, g: 2.6, bank: 40, violations: 3));
        Assert.NotEqual(MissionGrade.Failed, o.Grade);
        Assert.True(o.QualityMilli >= 55_000);
    }
}
