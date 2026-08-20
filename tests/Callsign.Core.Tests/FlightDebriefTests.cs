using System.Linq;
using Callsign.Core.Debrief;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 10a — the post-flight coaching debrief. Pure over recorded state: reward mastery, coach the
/// rest as "here's how", name a consequence only where one was billed (the Fun Dial, L9).</summary>
public class FlightDebriefTests
{
    private static DebriefInput Flight(
        int overall = 95, int landing = 95, int approach = 95, double fpm = -80, double g = 1.2,
        bool stable = true, int violations = 0, bool valid = true, int? outcome = null, string? reason = null,
        params (string Sev, string Msg)[] events)
        => new(true, valid, overall, landing, approach, fpm, g, stable, violations, outcome, reason,
            events.Select(e => new DebriefEvent(e.Sev, e.Msg)).ToList());

    [Fact]
    public void NotScored_HasNothingToGrade()
    {
        var r = FlightDebrief.Build(new DebriefInput(false, true, 0, 0, 0, 0, 0, false, 0, null, null, new List<DebriefEvent>()));
        Assert.False(r.Scored);
        Assert.Equal("Not graded", r.Grade);
        Assert.Empty(r.Strengths);
        Assert.Empty(r.ToImprove);
    }

    [Fact]
    public void TextbookLeg_LeadsWithStrengths_AndHasNothingToImprove()
    {
        var r = FlightDebrief.Build(Flight());
        Assert.Equal("Textbook", r.Grade);
        Assert.Empty(r.ToImprove);
        Assert.Contains(r.Strengths, n => n.Headline == "Greaser");
        Assert.Contains(r.Strengths, n => n.Headline == "Stabilised approach");
        Assert.Contains(r.Strengths, n => n.Headline == "Clean handling");
        Assert.All(r.Strengths, n => Assert.Equal(DebriefTone.Strength, n.Tone));
        Assert.Contains("nothing to pick at", r.Headline);
    }

    [Fact]
    public void FirmLanding_IsCoachedFromTheScore_EvenWithNoWarningEvent()
    {
        var r = FlightDebrief.Build(Flight(overall: 70, landing: 65, fpm: -320, g: 1.5)); // a firm-but-not-warned touchdown
        var note = Assert.Single(r.ToImprove, n => n.Dimension == "Landing");
        Assert.Equal(DebriefTone.Coaching, note.Tone);           // 65 → coach, not a consequence
        Assert.Contains("320", note.Headline);
        Assert.NotEmpty(note.Detail);                            // a concrete "how to fix"
        Assert.Contains("landing", r.Headline);
    }

    [Fact]
    public void HardLanding_BelowThreshold_IsAConsequence()
        => Assert.Equal(DebriefTone.Consequence,
            Assert.Single(FlightDebrief.Build(Flight(overall: 40, landing: 30, fpm: -700, g: 2.4)).ToImprove, n => n.Dimension == "Landing").Tone);

    [Fact]
    public void UnstableApproach_IsCoached()
    {
        var r = FlightDebrief.Build(Flight(overall: 70, stable: false, approach: 55));
        Assert.Contains(r.ToImprove, n => n.Dimension == "Approach" && n.Headline == "Unstable approach");
    }

    [Fact]
    public void WarningEvent_BecomesAConsequence_WithCoaching()
    {
        var r = FlightDebrief.Build(Flight(overall: 55, violations: 15, events: ("Warning", "Overspeed warning")));
        var note = Assert.Single(r.ToImprove, n => n.Dimension == "Speed");
        Assert.Equal(DebriefTone.Consequence, note.Tone);
        Assert.Contains("Vne", note.Detail);                    // a specific, actionable fix
    }

    [Fact]
    public void CoachingEvent_StaysAGentleNote()
    {
        var r = FlightDebrief.Build(Flight(events: ("Coaching", "Bank 42° — ease it back")));
        var note = Assert.Single(r.ToImprove, n => n.Dimension == "Bank");
        Assert.Equal(DebriefTone.Coaching, note.Tone);
        Assert.NotEmpty(note.Detail);
    }

    [Fact]
    public void ApproachDriftCoaching_SurfacesAsAnApproachNote()
        => Assert.Contains(FlightDebrief.Build(Flight(events: ("Coaching", "Drifting right of the centreline — small, early corrections back onto the approach"))).ToImprove,
            n => n.Dimension == "Approach" && n.Tone == DebriefTone.Coaching);

    [Fact]
    public void EngineStressCoaching_IsSurfaced()
        => Assert.Contains(FlightDebrief.Build(Flight(events: ("Coaching", "Engine stress registering — ease the power and watch your temps"))).ToImprove,
            n => n.Dimension == "Engine" && n.Tone == DebriefTone.Coaching);

    [Fact]
    public void VoidedScore_GradesVoided_AndExplainsTheForfeit()
    {
        var r = FlightDebrief.Build(Flight(overall: 96, valid: false, events: ("Warning", "Slew detected — score void")));
        Assert.Equal("Voided", r.Grade);
        Assert.Contains(r.ToImprove, n => n.Dimension == "Integrity");
    }

    [Fact]
    public void FailedDelivery_IsAConsequence()
        => Assert.Contains(FlightDebrief.Build(Flight(outcome: 3, reason: "the cargo was destroyed on a slam landing")).ToImprove,
            n => n.Dimension == "Delivery" && n.Tone == DebriefTone.Consequence);

    [Fact]
    public void RoutineEvents_AreNotDuplicatedAsFaults()
    {
        var r = FlightDebrief.Build(Flight(events: new[] { ("Info", "Takeoff"), ("Success", "Landed at -80 fpm"), ("Info", "Tough conditions — 25 kt crosswind — +8 to the landing grade") }));
        Assert.Empty(r.ToImprove); // takeoff / the landing line / the conditions note are covered elsewhere or aren't faults
    }
}
