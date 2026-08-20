namespace Callsign.Core.Debrief;

/// <summary>The voice of a debrief note (the Fun Dial, L9): a strength earned, a coaching nudge, or a
/// real consequence that was actually billed.</summary>
public enum DebriefTone { Strength, Coaching, Consequence }

/// <summary>One line of the debrief: WHAT happened + HOW to improve (or what you did well).</summary>
public sealed record DebriefNote(DebriefTone Tone, string Dimension, string Headline, string Detail);

/// <summary>A single recorded flight moment (severity + message), as persisted on the Flight (Phase 7a).</summary>
public sealed record DebriefEvent(string Severity, string Message);

/// <summary>The scored facts of a settled flight, distilled from the persisted <c>Flight</c> (nullable scores
/// collapsed to concrete values). Pure input so the debrief is trivially testable.</summary>
public sealed record DebriefInput(
    bool Scored, bool ScoreValid, int OverallScore, int LandingScore, int ApproachScore,
    double TouchdownFpm, double TouchdownG, bool StabilizedApproach, int ViolationPoints,
    int? OutcomeGrade, string? OutcomeReason, IReadOnlyList<DebriefEvent> Events);

/// <summary>The structured post-flight coaching report.</summary>
public sealed record DebriefReport(
    bool Scored, bool ScoreValid, int OverallScore, string Grade, string Headline,
    IReadOnlyList<DebriefNote> Strengths, IReadOnlyList<DebriefNote> ToImprove);

/// <summary>
/// Phase 10a — the post-flight coaching debrief. A PURE function that turns everything the flight already
/// recorded (the 9c landing/approach/enroute scores + the persisted 9a coaching and 7c warning events) into
/// feedback the pilot can act on. On the Fun Dial (L9): lead with what went well, coach the rest as "here's
/// how", and only name a consequence where one was actually billed. It never scores or bills anything — it
/// only EXPLAINS what already happened, which is the payoff for every scored signal Phases 7–9 recorded.
/// </summary>
public static class FlightDebrief
{
    public static DebriefReport Build(DebriefInput f)
    {
        if (!f.Scored)
            return new DebriefReport(false, true, 0, "Not graded",
                "This leg wasn't flown with live telemetry, so there's nothing to grade — fly it in the sim for a full debrief.",
                Array.Empty<DebriefNote>(), Array.Empty<DebriefNote>());

        var strengths = new List<DebriefNote>();
        var improve = new List<DebriefNote>();
        double fpm = Math.Abs(f.TouchdownFpm);

        // ── Landing — reward a greaser; coach a firm one from the SCORE (the raw event may not flag a 320 fpm touchdown) ──
        if (f.LandingScore >= 90 && fpm <= 200 && f.TouchdownG <= 1.4)
            strengths.Add(new(DebriefTone.Strength, "Landing", "Greaser",
                $"Touched down at {fpm:F0} fpm and {f.TouchdownG:F1} g — smooth as it gets."));
        else if (f.LandingScore < 80)
        {
            var tone = f.LandingScore < 55 ? DebriefTone.Consequence : DebriefTone.Coaching;
            string how = fpm > 240 && f.TouchdownG > 1.8
                ? "Both the sink rate and the load were high — start the flare a touch earlier and let it settle."
                : fpm > 240
                    ? "The sink rate did it — arrest it in the flare, cross the threshold a little slower, and aim to settle under 200 fpm."
                    : f.TouchdownG > 1.8
                        ? "The load spiked at touchdown — a smoother round-out spreads it; ease the yoke, don't check it hard."
                        : "A little firm — hold it off a moment longer in the flare and let the mains kiss the runway.";
            improve.Add(new(tone, "Landing", $"Firm touchdown — {fpm:F0} fpm, {f.TouchdownG:F1} g", how));
        }

        // ── Approach ──
        if (f.StabilizedApproach && f.ApproachScore >= 85)
            strengths.Add(new(DebriefTone.Strength, "Approach", "Stabilised approach",
                "Configured, on speed and on profile by the gate — exactly how it should look."));
        else if (!f.StabilizedApproach || f.ApproachScore < 75)
            improve.Add(new(DebriefTone.Coaching, "Approach", "Unstable approach",
                "Below 1,000 ft you drifted outside the gate (rate, bank or speed). Be configured and on-speed by the gate — and if it isn't stable, go around. There's no prize for salvaging a bad approach."));

        // ── Enroute — a clean leg is a strength; the recorded exceedances become notes ──
        if (f.ViolationPoints == 0)
            strengths.Add(new(DebriefTone.Strength, "Enroute", "Clean handling", "No exceedances the whole leg — steady hands."));

        foreach (var e in f.Events)
        {
            if (SkipInDebrief(e.Message)) continue; // takeoff / the landing line / conditions note / already-synthesised
            if (e.Severity == "Warning")
                improve.Add(new(DebriefTone.Consequence, DimensionOf(e.Message), e.Message, CoachFor(e.Message)));
            else if (e.Severity == "Coaching")
                improve.Add(new(DebriefTone.Coaching, DimensionOf(e.Message), e.Message, CoachFor(e.Message)));
        }

        // ── Integrity / delivery ──
        if (!f.ScoreValid)
            improve.Add(new(DebriefTone.Consequence, "Integrity", "Score voided",
                "Flight-integrity monitoring flagged this leg (slew, or time-acceleration near the ground), so the performance bonus was forfeit. Fly it clean for the reward."));
        if (f.OutcomeGrade is 3)
            improve.Add(new(DebriefTone.Consequence, "Delivery", "Delivery failed", f.OutcomeReason ?? "The cargo didn't arrive in a deliverable state."));
        else if (f.OutcomeGrade is 2)
            improve.Add(new(DebriefTone.Coaching, "Delivery", "Partial delivery", f.OutcomeReason ?? "The cargo arrived, but not in perfect shape."));

        string grade = !f.ScoreValid ? "Voided" : GradeFor(f.OverallScore);
        return new DebriefReport(true, f.ScoreValid, f.OverallScore, grade, HeadlineFor(grade, improve), strengths, improve);
    }

    private static string GradeFor(int score) => score switch
    {
        >= 90 => "Textbook",
        >= 75 => "Solid",
        >= 55 => "Workmanlike",
        >= 35 => "Rough",
        _ => "Unsafe",
    };

    private static string HeadlineFor(string grade, List<DebriefNote> improve)
    {
        if (grade == "Voided") return "The score was voided — fly it clean and the grade (and the bonus) are yours.";
        if (improve.Count == 0) return $"A {grade.ToLowerInvariant()} leg — nothing to pick at. Keep flying like this.";
        if (grade is "Textbook" or "Solid") return $"A {grade.ToLowerInvariant()} leg overall — a couple of small things to sharpen.";
        return $"{grade} — the biggest gains are in your {improve[0].Dimension.ToLowerInvariant()}.";
    }

    // The "Landed at … fpm" line and "Unstable approach" are already covered by the synthesised landing/approach
    // notes; takeoff and tough-conditions are not faults.
    private static bool SkipInDebrief(string m)
        => m.StartsWith("Landed at", StringComparison.Ordinal)
        || m.StartsWith("Unstable approach", StringComparison.Ordinal)
        || m.StartsWith("Takeoff", StringComparison.Ordinal)
        || m.StartsWith("Tough conditions", StringComparison.Ordinal);

    private static string DimensionOf(string m)
    {
        if (m.Contains("Overspeed")) return "Speed";
        if (m.Contains("Stall")) return "Stall";
        if (m.Contains("bank", StringComparison.OrdinalIgnoreCase)) return "Bank";
        if (m.Contains("load", StringComparison.OrdinalIgnoreCase) || m.Contains(" g")) return "Load";
        if (m.Contains("MTOW") || m.Contains("Overweight")) return "Weight";
        if (m.Contains("CG")) return "Balance";
        if (m.Contains("Side-load")) return "Landing";
        if (m.Contains("Engine stress")) return "Engine";
        if (m.Contains("Taxi")) return "Taxi";
        if (m.Contains("Slew") || m.Contains("Time acceleration")) return "Integrity";
        return "Handling";
    }

    private static string CoachFor(string m)
    {
        if (m.Contains("Overspeed")) return "You went past Vne/Vmo. Reduce power and raise the nose before the airframe complains — an overspeed can do real structural damage.";
        if (m.Contains("Stall")) return "The wing stalled. Lower the nose to break the stall and add power; keep your margins, especially in the turn to final.";
        if (m.Contains("Steep bank")) return "You rolled past a safe bank angle. Keep it under 30° in the pattern and coordinate the turn.";
        if (m.Contains("Bank")) return "That bank was a little steep. Ease it back — smooth, shallow turns down low.";
        if (m.Contains("High load")) return "You pulled or pushed hard. Smooth, gradual inputs keep the load in check and the cabin comfortable.";
        if (m.Contains("Load")) return "The load crept up. Gentle on the controls — your passengers feel every g.";
        if (m.Contains("Overweight")) return "You launched over max weight. Trim the fuel or payload — an overweight take-off eats runway and climb performance.";
        if (m.Contains("MTOW")) return "You were a hair over max weight. Watch the loading — it adds up.";
        if (m.Contains("CG")) return "The centre of gravity was near or past a limit. Recheck the load sheet — an out-of-CG airplane handles poorly and can be unrecoverable.";
        if (m.Contains("Side-load")) return "You touched down with drift on. De-crab just before the wheels touch — rudder to align, aileron into the wind.";
        if (m.Contains("Engine stress")) return "The engine took real stress this leg. Ease the power and mind your temps and RPM — abuse shortens its life and shows up at maintenance.";
        if (m.Contains("Taxi")) return "You taxied fast. Keep it to a brisk walking pace on the ground.";
        if (m.Contains("Slew") || m.Contains("Time acceleration")) return "That voided the score. Fly the leg in real time without slewing for the full grade and bonus.";
        return "";
    }
}
