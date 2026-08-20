namespace Callsign.Core.World;

/// <summary>
/// The world calendar (Phase 8): the legible "what day/season is it" every timing mechanic reads. World time
/// runs on the same elapsed wall-clock the money already accrues on (wages/rent/loans), anchored at the
/// company's founding — so the calendar and the ledger never drift. A pure function of the instant (and, for
/// the season, a latitude), like the rest of the world model.
/// </summary>
public static class GameCalendar
{
    private static readonly string[] Seasons = { "Spring", "Summer", "Autumn", "Winter" };

    /// <summary>The meteorological season at a latitude for a moment — hemisphere-correct (the South is flipped).</summary>
    public static string Season(DateTimeOffset instant, double latitude)
    {
        int north = instant.UtcDateTime.Month switch
        {
            3 or 4 or 5 => 0,     // Mar–May  → Spring
            6 or 7 or 8 => 1,     // Jun–Aug  → Summer
            9 or 10 or 11 => 2,   // Sep–Nov  → Autumn
            _ => 3,               // Dec–Feb  → Winter
        };
        int idx = latitude >= 0 ? north : (north + 2) % 4; // Southern hemisphere is half a year out of phase
        return Seasons[idx];
    }

    /// <summary>Whole days a company has been operating, from its founding instant to now (never negative).</summary>
    public static int CareerDays(DateTimeOffset foundedAt, DateTimeOffset now)
        => Math.Max(0, (int)(now - foundedAt).TotalDays);
}
