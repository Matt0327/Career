namespace Callsign.Core.Economy;

/// <summary>A borrowing tier: the principal band it covers and its APR. Larger loans get a lower rate.</summary>
public sealed record LoanTierDef(int Tier, string Name, long MinPrincipalCents, long MaxPrincipalCents, int AprBps);

/// <summary>
/// The lending tiers (Phase 4a). Reference content — self-documenting, so the UI never hard-codes a rate.
/// Bigger principals earn a lower APR (the bank's confidence scales with the deal). Freely retunable.
/// </summary>
public static class LoanCatalog
{
    public static readonly IReadOnlyList<LoanTierDef> Tiers =
    [
        new(1, "Starter",    100_000,       5_000_000,   1_400), // up to $50k  @ 14.0%
        new(2, "Business",   5_000_001,     50_000_000,  1_000), // up to $500k @ 10.0%
        new(3, "Commercial", 50_000_001,    500_000_000, 700),   // up to $5M   @  7.0%
        new(4, "Fleet",      500_000_001,   5_000_000_000, 500), // up to $50M  @  5.0%
    ];

    /// <summary>The tier a principal falls in, or null if it's outside every band.</summary>
    public static LoanTierDef? TierFor(long principalCents)
    {
        foreach (var t in Tiers)
            if (principalCents >= t.MinPrincipalCents && principalCents <= t.MaxPrincipalCents)
                return t;
        return null;
    }

    public static long MaxPrincipalCents => Tiers[^1].MaxPrincipalCents;
    public static long MinPrincipalCents => Tiers[0].MinPrincipalCents;

    /// <summary>A letter grade for a 0–100 credit score — the human-legible face of the number (Phase 7g).</summary>
    public static string CreditGrade(int score) => score switch
    {
        >= 85 => "A",
        >= 70 => "B",
        >= 55 => "C",
        >= 40 => "D",
        _ => "E",
    };

    /// <summary>
    /// Interest + principal accrued over <paramref name="days"/>: declining-balance interest (on the
    /// shrinking outstanding) with a straight-line principal repayment (original ÷ term). Pure.
    /// </summary>
    public static (long Interest, long Principal) Amortize(long outstandingCents, long originalPrincipalCents, int aprBps, int termDays, int days)
    {
        double dailyRate = aprBps / 10_000.0 / 365.0;
        long dailyPrincipal = (long)Math.Ceiling((double)originalPrincipalCents / Math.Max(1, termDays));
        long interest = 0, principal = 0, bal = outstandingCents;
        for (int d = 0; d < days && bal > 0; d++)
        {
            interest += (long)Math.Round(bal * dailyRate);
            long pay = Math.Min(bal, dailyPrincipal);
            principal += pay;
            bal -= pay;
        }
        return (interest, principal);
    }
}

