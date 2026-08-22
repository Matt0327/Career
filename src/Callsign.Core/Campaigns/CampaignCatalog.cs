using Callsign.Core.Domain;
using Callsign.Core.Progression;

namespace Callsign.Core.Campaigns;

/// <summary>
/// One step of a campaign: a narrative objective plus the metric that satisfies it. Like an achievement,
/// it reads a number off the shared <see cref="ProgressMetrics"/> snapshot against a <see cref="Target"/>,
/// so a step also yields a progress bar. Steps are completed <em>in order</em>.
/// </summary>
public sealed record CampaignStepDef(string Title, string Detail, long Target, Func<ProgressMetrics, long> Metric)
{
    public long ProgressOf(ProgressMetrics m) => Math.Max(0, Metric(m));
    public bool IsMetBy(ProgressMetrics m) => Metric(m) >= Target;
}

/// <summary>
/// An authored story arc (Phase 5b): an ordered chain of steps and a cash reward paid — through the
/// ledger, like any other money — when the last step is cleared. Self-documenting and server-suppliable;
/// only a company's <em>position</em> in an arc is persisted (a <see cref="CampaignProgress"/>).
/// </summary>
public sealed record CampaignDef(
    string Key, string Name, string Description, long RewardCents, IReadOnlyList<CampaignStepDef> Steps);

/// <summary>
/// The campaign roster. Arcs are independent and all active from the start; each is a ladder of escalating
/// goals that reuses the same progress metrics the rest of the game already tracks.
/// </summary>
public static class CampaignCatalog
{
    public static readonly IReadOnlyList<CampaignDef> All = new[]
    {
        new CampaignDef("first-contract", "First Contract",
            "From an empty logbook to a company that turns a profit.", 25_000_00, new[]
            {
                new CampaignStepDef("Wheels up", "Settle your first job.", 1, m => m.Flights),
                new CampaignStepDef("Your own wings", "Own an aircraft.", 1, m => m.Aircraft),
                new CampaignStepDef("A clean touch", "Land within 60 fpm.", 1, m => m.SmoothLandings),
                new CampaignStepDef("Turning a profit", "Reach a net worth of $100,000.", 100_000_00, m => m.NetWorthCents),
            }),

        new CampaignDef("build-an-airline", "Build an Airline",
            "Grow one aircraft into a network with a name.", 150_000_00, new[]
            {
                new CampaignStepDef("Second station", "Operate two bases.", 2, m => m.Bases),
                new CampaignStepDef("A growing fleet", "Own three aircraft.", 3, m => m.Aircraft),
                new CampaignStepDef("On the network", "Open a scheduled route.", 1, m => m.Routes),
                new CampaignStepDef("Making the grade", "Reach the rank of Captain.", (int)PilotRank.Captain, m => m.RankIndex),
                new CampaignStepDef("Seven figures", "Reach a net worth of $1,000,000.", 1_000_000_00, m => m.NetWorthCents),
            }),

        new CampaignDef("the-long-haul", "The Long Haul",
            "Put in the hours and become a pilot people trust with anything.", 75_000_00, new[]
            {
                new CampaignStepDef("Building hours", "Settle 25 flights.", 25, m => m.Flights),
                new CampaignStepDef("Smooth operator", "Grease 15 landings (within 60 fpm).", 15, m => m.SmoothLandings),
                new CampaignStepDef("Type-rated", "Earn three qualifications.", 3, m => m.Qualifications),
                new CampaignStepDef("Well regarded", "Reach a pilot reputation of 50.", 50_000, m => m.ReputationMilli),
            }),

        new CampaignDef("sound-books", "Sound Books",
            "Run the company like a business — insured, out of debt, money in reserve.", 60_000_00, new[]
            {
                new CampaignStepDef("Covered", "Insure an aircraft.", 1, m => m.Policies),
                new CampaignStepDef("Square with the bank", "Pay off a loan in full.", 1, m => m.LoansPaidOff),
                new CampaignStepDef("Not all your eggs", "Own two aircraft.", 2, m => m.Aircraft),
                new CampaignStepDef("Reserves", "Reach a net worth of $500,000.", 500_000_00, m => m.NetWorthCents),
            }),

        new CampaignDef("flag-carrier", "Flag Carrier",
            "The long game: a trusted name flying a real network from coast to coast.", 500_000_00, new[]
            {
                new CampaignStepDef("Trusted name", "Reach an operating reputation of 50.", 50_000, m => m.OperatingReputationMilli),
                new CampaignStepDef("A real network", "Run five routes.", 5, m => m.Routes),
                new CampaignStepDef("Coast to coast", "Operate four bases.", 4, m => m.Bases),
                new CampaignStepDef("The top seat", "Reach the rank of Chief.", (int)PilotRank.Chief, m => m.RankIndex),
                new CampaignStepDef("Flag carrier", "Reach a net worth of $5,000,000.", 5_000_000_00, m => m.NetWorthCents),
            }),
    };
}
