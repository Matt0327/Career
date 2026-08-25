namespace Callsign.Core.Economy;

/// <summary>
/// The living demand model for a scheduled passenger route (Phase 14a). The seat load factor is no longer frozen
/// at creation — each reconcile it is computed from the airline's CURRENT operating reputation, the calendar
/// season, and the fare the player set. Pure and deterministic (reputation is a stored value; the season is a
/// function of the calendar), and structurally pump-free: revenue = seats × load × a frozen per-seat yield × fare,
/// which is income-only (a one-way passenger fare with no counterparty), the load factor is hard-bounded, and
/// none of these inputs ever reach the two-sided commodity market — so the weather+rep ≤ 2×spread guard is intact.
/// </summary>
public static class ScheduledDemand
{
    /// <summary>A smooth seasonal multiplier over the calendar year — a mid-summer leisure peak and a mid-winter
    /// trough, bounded to ±<see cref="EconomyConfig.ScheduledSeasonSwing"/>. Peak near day 196 (mid-July).</summary>
    public static double SeasonMultiplier(EconomyConfig cfg, DateTimeOffset when)
    {
        double phase = 2 * Math.PI * (when.DayOfYear - 196) / 365.0;
        return 1.0 + cfg.ScheduledSeasonSwing * Math.Cos(phase);
    }

    /// <summary>Fare price-elasticity: a fare above the market thins the cabin; a discount fills a few more seats.
    /// Neutral (1.0) at the market fare (1000). Bounded so a deep discount can't conjure a &gt;full plane and a
    /// steep premium still sells a token cabin.</summary>
    public static double FareLoadMultiplier(EconomyConfig cfg, int fareMultiplierMilli)
    {
        double m = fareMultiplierMilli / 1000.0;
        return Math.Clamp(1.0 - cfg.ScheduledFareElasticity * (m - 1.0), 0.15, 1.30);
    }

    /// <summary>The live seat load factor (thousandths) for a scheduled route this pass: the reputation-driven base
    /// fill, flexed by the season and the fare, hard-bounded to [min, max].</summary>
    public static int LoadFactorMilli(EconomyConfig cfg, int operatingRepMilli, double seasonMult, int fareMultiplierMilli)
    {
        int repLoad = cfg.ScheduledLoadFactorMilli(operatingRepMilli);       // base + rep bonus, already capped
        int load = (int)Math.Round(repLoad * seasonMult * FareLoadMultiplier(cfg, fareMultiplierMilli));
        return Math.Clamp(load, cfg.ScheduledMinLoadFactorMilli, cfg.ScheduledMaxLoadFactorMilli);
    }

    /// <summary>Revenue for one scheduled round trip: filled seats × per-seat yield × fare. The fare lifts the
    /// price per seat here while <see cref="LoadFactorMilli"/> thins the seats sold — the yield-management tradeoff.</summary>
    public static long RevenuePerTripCents(int seats, int loadFactorMilli, long seatYieldCents, int fareMultiplierMilli)
        => (long)Math.Round(seats * (loadFactorMilli / 1000.0) * seatYieldCents * (fareMultiplierMilli / 1000.0));

    /// <summary>Clamp a requested fare to the allowed scheduled band.</summary>
    public static int ClampFare(EconomyConfig cfg, int fareMultiplierMilli)
        => Math.Clamp(fareMultiplierMilli, cfg.MinScheduledFareMilli, cfg.MaxScheduledFareMilli);
}
