using Callsign.Core.Progression;

namespace Callsign.Core.Airline;

/// <summary>One requirement on a career stage: what it measures, your live value, the bar, and whether you clear
/// it. <c>Current</c>/<c>Target</c> are raw comparable longs (rank index, count, rep-milli, cents); <c>Display</c>
/// is pre-formatted for a dumb UI. <c>Primary</c> marks the two "spine" levers (rank + operating reputation).</summary>
public sealed record StageRequirement(
    string Label, string Metric, long Current, long Target, string Display, bool Met, bool Primary);

/// <summary>One thing a stage opens. <c>Live</c> is the honesty flag: true = works in the game today; false = a
/// named Phase-11 horizon (11c/11d/11e/11f), rendered as aspirational — never promised as shipped.</summary>
public sealed record StageUnlock(string Text, bool Live);

/// <summary>A named rung of the career ladder: its gate (all requirements AND-ed), what it opens, and whether
/// you've reached it.</summary>
public sealed record CareerStage(
    int Index, string Name, string Tagline, bool Reached,
    IReadOnlyList<StageRequirement> Requirements, IReadOnlyList<StageUnlock> Unlocks);

/// <summary>The single binding next step toward the next stage, plus the met/total that fills the progress bar.
/// Null once you're at the top rung (Flag Carrier).</summary>
public sealed record NextMove(
    int StageIndex, string StageName, int MetCount, int TotalCount, int ProgressPct,
    string Metric, string Label, string Display);

/// <summary>
/// Phase 11b — the career-stage journey. A PURE, DB-free catalog (the <c>CertificateCatalog</c> idiom) that
/// deepens the flat airline standing into a named, climbable ladder — Contract Operator → Charter Operator →
/// Regional → National → Flag Carrier — computed from the <see cref="ProgressMetrics"/> snapshot the standing
/// already reads. Each stage is a multi-requirement AND-gate; you stand on the highest stage whose whole gate
/// (and every gate below it) is met. It GATES NOTHING (L8): it returns a label, a checklist, and a "next move"
/// nudge — the real action-gates stay in the certificate service. It DUPLICATES NOTHING (L11): one catalog, one
/// deepened read model, no new table/column/money. Thresholds are monotonic across stages (a locked test guards
/// it), so a reached stage strictly dominates every lower one and no rung is unreachable.
/// </summary>
public static class CareerLadder
{
    private static readonly string[] RankNames = { "Trainee", "Copilot", "Captain", "Senior Captain", "Chief" };

    private sealed record ReqDef(string Metric, long Target, bool Primary);
    private sealed record StageDef(int Index, string Name, string Tagline, ReqDef[] Reqs, StageUnlock[] Unlocks);

    // Spine-first tiebreak when two unmet requirements are equally far from their bar.
    private static int Priority(string m) => m switch
    { "rank" => 0, "opRep" => 1, "netWorth" => 2, "fleet" => 3, "bases" => 4, "routes" => 5, _ => 9 };

    private static readonly StageDef[] Defs =
    {
        new(0, "Contract Operator", "One tail, other people's overflow.",
            Array.Empty<ReqDef>(),
            new[]
            {
                new StageUnlock("Fly the open job board, own and maintain your own aircraft, hire your first crew, and set autonomous standing orders that fly while you're away.", true),
                new StageUnlock("Contract-for-a-carrier legs — steady, lower-margin work flying the majors' overflow, the distinct flavour of the bottom rung. On the Phase-11 build list, not yet in the game.", false),
            }),
        new(1, "Charter Operator", "A second seat, a second tail, a name that's begun to register.",
            new[] { new ReqDef("rank", 1, true), new ReqDef("opRep", 10_000, true), new ReqDef("fleet", 2, false), new ReqDef("netWorth", 10_000_000, false) },
            new[]
            {
                new StageUnlock("You're at the scale where the Air Charter Certificate earns its keep — premium VIP passenger charter. The stage signposts it; the certificate keeps its own fee-and-standards gate (apply in the Certificates panel below).", true),
                new StageUnlock("Your operating reputation now visibly compounds in your standing — every clean leg you fly and every good crew you keep moves the name.", true),
            }),
        new(2, "Regional", "A fleet, more than one field, a name that travels.",
            new[] { new ReqDef("rank", 2, true), new ReqDef("opRep", 25_000, true), new ReqDef("fleet", 4, false), new ReqDef("bases", 2, false), new ReqDef("routes", 3, false), new ReqDef("netWorth", 60_000_000, false) },
            new[]
            {
                new StageUnlock("Run multiple bases and scheduled routes autonomously — a real regional network flying while you're away.", true),
                new StageUnlock("Your operating reputation begins to lift demand and pay at your hubs — the flywheel's first turn. Designed, not yet switched on (11c).", false),
            }),
        new(3, "National", "Scheduled service across the country.",
            new[] { new ReqDef("rank", 3, true), new ReqDef("opRep", 50_000, true), new ReqDef("fleet", 7, false), new ReqDef("bases", 3, false), new ReqDef("routes", 8, false), new ReqDef("netWorth", 250_000_000, false) },
            new[]
            {
                new StageUnlock("A network at national scale — a deep owned fleet and scheduled service spanning your bases, run autonomously.", true),
                new StageUnlock("Promote a base to a hub: scheduled-slot capacity scales with hub level × operating reputation. Named so the climb points somewhere real — not yet built (11e).", false),
            }),
        new(4, "Flag Carrier", "The name at the top of the board.",
            new[] { new ReqDef("rank", 4, true), new ReqDef("opRep", 75_000, true), new ReqDef("fleet", 12, false), new ReqDef("bases", 5, false), new ReqDef("routes", 16, false), new ReqDef("netWorth", 800_000_000, false) },
            new[]
            {
                new StageUnlock("The Air Operator Certificate and a true scheduled-passenger network — real seats, frequencies, load factors and seat-class yield, booked offline. The marquee endgame, still to be built (11f).", false),
                new StageUnlock("The summit of the standing — you fly the flag, and every operator on the leaderboard is now measured against you.", true),
            }),
    };

    /// <summary>Project a progress snapshot onto the ladder: the current stage, all five rungs (with per-requirement
    /// met/current/target), and the single binding next move (null at the top). Pure — no DB, clock, or money.</summary>
    public static (int CurrentStage, IReadOnlyList<CareerStage> Stages, NextMove? Move) Evaluate(ProgressMetrics m)
    {
        var stages = new CareerStage[Defs.Length];
        var gateMet = new bool[Defs.Length];
        for (int i = 0; i < Defs.Length; i++)
        {
            var reqs = Defs[i].Reqs.Select(r => BuildReq(m, r)).ToList();
            gateMet[i] = reqs.All(r => r.Met);                       // empty gate ⇒ true (Stage 0)
            stages[i] = new CareerStage(Defs[i].Index, Defs[i].Name, Defs[i].Tagline, false, reqs, Defs[i].Unlocks);
        }

        int current = 0;
        for (int i = 1; i < Defs.Length; i++) { if (gateMet[i]) current = i; else break; } // highest contiguous met run
        for (int i = 0; i < stages.Length; i++) stages[i] = stages[i] with { Reached = i <= current };

        NextMove? move = null;
        if (current < Defs.Length - 1)
        {
            var next = stages[current + 1];
            int met = next.Requirements.Count(r => r.Met), total = next.Requirements.Count;
            var dom = next.Requirements.Where(r => !r.Met)
                .OrderBy(Fraction).ThenBy(r => Priority(r.Metric)).First();
            move = new NextMove(next.Index, next.Name, met, total, total == 0 ? 100 : met * 100 / total,
                                dom.Metric, Headline(dom, next.Name), dom.Display);
        }
        return (current, stages, move);
    }

    private static double Fraction(StageRequirement r) => r.Target <= 0 ? 1 : Math.Clamp((double)r.Current / r.Target, 0, 1);

    private static StageRequirement BuildReq(ProgressMetrics m, ReqDef r)
    {
        long cur = r.Metric switch
        {
            "rank" => m.RankIndex,
            "opRep" => m.OperatingReputationMilli,
            "fleet" => m.Aircraft,
            "bases" => m.Bases,
            "routes" => m.Routes,
            "netWorth" => m.NetWorthCents,
            _ => 0,
        };
        bool met = cur >= r.Target;
        return new StageRequirement(Label(r.Metric, r.Target), r.Metric, cur, r.Target,
                                    Display(r.Metric, cur, r.Target, met), met, r.Primary);
    }

    private static string Rank(long i) => RankNames[Math.Clamp((int)i, 0, 4)];

    private static string Money(long cents)
    {
        string sign = cents < 0 ? "-" : "";          // a company underwater on a loan can read a negative net worth
        double d = Math.Abs(cents) / 100.0;
        string body = d >= 1_000_000 ? $"${d / 1_000_000:0.#}M" : d >= 1_000 ? $"${d / 1_000:0}k" : $"${d:0}";
        return sign + body;                          // "-$50k", not "$-50000"
    }

    private static string Label(string metric, long t) => metric switch
    {
        "rank" => $"{Rank(t)} rank",
        "opRep" => $"{t / 1000.0:0.0} operating reputation",
        "fleet" => $"{t} aircraft",
        "bases" => $"{t} bases",
        "routes" => $"{t} routes",
        "netWorth" => $"{Money(t)} net worth",
        _ => metric,
    };

    private static string Display(string metric, long cur, long t, bool met) => metric switch
    {
        "rank" => met ? Rank(cur) : $"{Rank(cur)} → {Rank(t)}",
        "opRep" => $"{cur / 1000.0:0.0} / {t / 1000.0:0.0}",
        "fleet" => $"{cur} / {t} aircraft",
        "bases" => $"{cur} / {t} bases",
        "routes" => $"{cur} / {t} routes",
        "netWorth" => $"{Money(cur)} / {Money(t)}",
        _ => $"{cur} / {t}",
    };

    private static string Headline(StageRequirement r, string next) => r.Metric switch
    {
        "rank" => $"Earn your {Rank(r.Target)} stripes — rank is the one thing {next} still needs from you.",
        "opRep" => $"Grow the name to {r.Target / 1000.0:0.0} — fly your marquee legs yourself and crew the rest well; a name is the one lever rivals can't buy.",
        "netWorth" => $"Build the book to {Money(r.Target)} — {next} wants a balance sheet that can carry it.",
        "fleet" => $"Add tails — {next} runs {r.Target} aircraft; you own {r.Current}.",
        "bases" => $"Open another base — {next} spans {r.Target} fields.",
        "routes" => $"Open more scheduled routes — {next} flies {r.Target}.",
        _ => $"Keep climbing toward {next}.",
    };
}
