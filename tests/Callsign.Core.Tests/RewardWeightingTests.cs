using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 13 — pay should be led by distance, then payload/passengers.</summary>
public class RewardWeightingTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;

    [Fact]
    public void Cargo_DistanceTermLeadsWeightTerm_OnATypicalLeg()
    {
        double nm = 200; int lbs = 1500;
        long distTerm = (long)System.Math.Round(nm * Cfg.CargoPerNmCents);
        long weightTerm = lbs * Cfg.CargoPerLbCents;
        Assert.True(distTerm > weightTerm, $"distance {distTerm} should lead weight {weightTerm}");
    }

    [Fact]
    public void Passenger_DistanceTermLeadsFlatPerSeat_OnATypicalLeg()
    {
        double nm = 200; int pax = 4;
        long distTerm = pax * (long)System.Math.Round(nm * Cfg.PaxPerPaxNmCents);
        long seatTerm = pax * Cfg.PaxPerPaxCents;
        Assert.True(distTerm > seatTerm, $"distance {distTerm} should lead per-seat {seatTerm}");
    }

    [Fact]
    public void LongerLeg_PaysMore_AllElseEqual()
    {
        Assert.True(Cfg.CargoRewardCents(400, 1000) > Cfg.CargoRewardCents(100, 1000));
        Assert.True(Cfg.PaxRewardCents(400, 4) > Cfg.PaxRewardCents(100, 4));
    }
}
