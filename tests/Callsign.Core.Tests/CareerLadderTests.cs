using System.Linq;
using Callsign.Core.Airline;
using Callsign.Core.Progression;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 11b — the career-stage journey. A pure projection of a <see cref="ProgressMetrics"/> snapshot
/// onto the named ladder (Contract Operator → Flag Carrier): a multi-requirement AND-gate, the highest
/// fully-met rung, and the single binding "next move". No DB, no clock, no money.</summary>
public class CareerLadderTests
{
    // A snapshot with only the six ladder levers set; everything else zero.
    private static ProgressMetrics Metrics(int rank, int opRep, int fleet, int bases, int routes, long netWorthCents)
        => new(0, 0, rank, 0, 0, fleet, bases, routes, 0, 0, netWorthCents, opRep);

    [Fact]
    public void FreshCareer_IsContractOperator()
    {
        var (current, stages, move) = CareerLadder.Evaluate(Metrics(rank: 0, opRep: 0, fleet: 1, bases: 1, routes: 0, netWorthCents: 0));
        Assert.Equal(0, current);
        Assert.True(stages[0].Reached);
        Assert.False(stages[1].Reached);
        Assert.Equal(5, stages.Count);
        Assert.NotNull(move);
        Assert.Equal("Charter Operator", move!.StageName);
        Assert.Equal(0, move.MetCount);
        Assert.Equal(4, move.TotalCount);
        Assert.Equal("rank", move.Metric); // rank/opRep/netWorth all at fraction 0 → spine-first tiebreak picks rank
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("opRep")]
    [InlineData("fleet")]
    [InlineData("bases")]
    [InlineData("routes")]
    [InlineData("netWorth")]
    public void AndGate_RegionalNeedsEveryRequirement(string drop)
    {
        int rank = 2, opRep = 25_000, fleet = 4, bases = 2, routes = 3; long nw = 60_000_000;
        Assert.Equal(2, CareerLadder.Evaluate(Metrics(rank, opRep, fleet, bases, routes, nw)).CurrentStage); // clears Regional on every lever

        switch (drop) // drop exactly one lever below its Regional bar
        {
            case "rank": rank = 1; break;
            case "opRep": opRep = 24_999; break;
            case "fleet": fleet = 3; break;
            case "bases": bases = 1; break;
            case "routes": routes = 2; break;
            case "netWorth": nw = 59_999_999; break;
        }
        Assert.Equal(1, CareerLadder.Evaluate(Metrics(rank, opRep, fleet, bases, routes, nw)).CurrentStage); // AND-gate: any single miss holds you at Charter
    }

    [Fact]
    public void SitsAtHighestFullyMetStage()
    {
        // Meets National on rank/rep/fleet/bases/routes but NOT net worth ($600k < $2.5M) → sits at Regional.
        var (current, stages, _) = CareerLadder.Evaluate(Metrics(rank: 3, opRep: 50_000, fleet: 7, bases: 3, routes: 8, netWorthCents: 60_000_000));
        Assert.Equal(2, current);
        Assert.True(stages[2].Reached);
        Assert.False(stages[3].Reached); // never a higher rung whose bars you only partially clear
        Assert.False(stages[4].Reached);
    }

    [Theory]
    [InlineData(2, 25_000, 4, 2, 1, 60_000_000L, "routes")] // only routes short → the binding constraint
    // rank & opRep met; fleet (1/2) and netWorth ($50k/$100k) TIE at fraction 0.5. fleet is defined BEFORE netWorth
    // in the catalog, so a stable sort alone would pick fleet — Priority(netWorth=2) < Priority(fleet=3) must win.
    // This genuinely exercises the tiebreak: drop `.ThenBy(Priority)` and this case flips to "fleet" and fails.
    [InlineData(1, 10_000, 1, 0, 0, 5_000_000L, "netWorth")]
    public void DominantLever_PicksBindingConstraintThenSpine(int rank, int opRep, int fleet, int bases, int routes, long nw, string expected)
        => Assert.Equal(expected, CareerLadder.Evaluate(Metrics(rank, opRep, fleet, bases, routes, nw)).Move!.Metric);

    [Fact]
    public void ThresholdsAreMonotonic()
    {
        var (_, stages, _) = CareerLadder.Evaluate(Metrics(4, 100_000, 99, 99, 99, 999_999_999));
        foreach (var metric in new[] { "rank", "opRep", "fleet", "bases", "routes", "netWorth" })
        {
            var targets = stages.SelectMany(s => s.Requirements).Where(r => r.Metric == metric).Select(r => r.Target).ToList();
            for (int i = 1; i < targets.Count; i++)
                Assert.True(targets[i] >= targets[i - 1], $"{metric} target decreased: {targets[i - 1]} -> {targets[i]}");
        }
    }

    [Fact]
    public void StageMetrics_Nest_SoTheContiguousRunIsSound()
    {
        // The "highest contiguous met run" == "highest reached stage" ONLY if each higher stage requires a SUPERSET
        // of every lower stage's metrics (so meeting stage N implies meeting all lower gates — no [met, unmet, met]
        // gap can form). Monotonic bars alone don't guarantee this; this locks the superset property too.
        var (_, stages, _) = CareerLadder.Evaluate(Metrics(4, 100_000, 99, 99, 99, 999_999_999));
        var prev = new HashSet<string>();
        foreach (var s in stages)
        {
            var metrics = s.Requirements.Select(r => r.Metric).ToHashSet();
            Assert.True(prev.IsSubsetOf(metrics), $"stage {s.Index} ({s.Name}) drops a metric a lower stage required");
            prev = metrics;
        }
    }

    [Fact]
    public void FlagCarrier_IsTopOfLadder()
    {
        var (current, stages, move) = CareerLadder.Evaluate(Metrics(4, 100_000, 99, 99, 99, 999_999_999));
        Assert.Equal(4, current);
        Assert.Equal("Flag Carrier", stages[4].Name);
        Assert.True(stages[4].Reached);
        Assert.Null(move); // no rung above → no next move
    }

    [Fact]
    public void Requirement_ReportsValuesAndDisplay()
    {
        var (_, stages, _) = CareerLadder.Evaluate(Metrics(rank: 1, opRep: 12_000, fleet: 3, bases: 1, routes: 8, netWorthCents: 120_000_000));
        var routes = stages[4].Requirements.Single(r => r.Metric == "routes"); // Flag Carrier needs 16
        Assert.Equal(8, routes.Current);
        Assert.Equal(16, routes.Target);
        Assert.False(routes.Met);
        Assert.Equal("8 / 16 routes", routes.Display);
        Assert.Equal("$1.2M / $2.5M", stages[3].Requirements.Single(r => r.Metric == "netWorth").Display);
        Assert.Equal("Copilot → Captain", stages[2].Requirements.Single(r => r.Metric == "rank").Display);
    }

    [Fact]
    public void Unlocks_HorizonFlagsAreHonest()
    {
        var (_, stages, _) = CareerLadder.Evaluate(Metrics(0, 0, 1, 1, 0, 0));
        Assert.Contains(stages[0].Unlocks, u => u.Live); // shipped mechanics are marked live
        Assert.Contains(stages[2].Unlocks, u => !u.Live && u.Text.Contains("hub", StringComparison.OrdinalIgnoreCase)); // 11c hub demand is aspirational
        Assert.Contains(stages[4].Unlocks, u => !u.Live && u.Text.Contains("Air Operator Certificate")); // 11f AOC is aspirational
    }
}
