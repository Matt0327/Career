using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Callsign.Core.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 11d — the contract on-ramp. Invented-carrier overflow work: ordinary Cargo jobs (existing
/// settlement path, no new money seam) priced BELOW the freelance owner rate, branded to the carrier as client,
/// and — unlike your own freelance work — never taking the 11c hub-reputation lift.</summary>
public class ContractOnRampTests
{
    private static readonly EconomyConfig Cfg = EconomyConfig.Default;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_GeneratesContractCargo_BelowFreelanceRate()
    {
        // All within a Trainee's reach (Phase 12 rank filter), so the pay formula is exercised across distances.
        var candidates = new List<JobCandidate> { new("EHRD", 30), new("EHEH", 55), new("EHGG", 80) };
        var jobs = new ContractJobSource(Cfg).Generate(new JobGenerationRequest("EHAM", candidates, PilotRank.Trainee, 6, 42));

        Assert.NotEmpty(jobs);
        Assert.All(jobs, j =>
        {
            Assert.Equal(MissionType.Cargo, j.Type);                         // settles through the existing cargo path
            Assert.False(string.IsNullOrEmpty(j.ContractCarrier));           // tagged with its carrier
            long freelance = Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs);
            Assert.Equal((long)Math.Round(freelance * Cfg.ContractCarrierPayFactor), j.RewardCents);
            Assert.True(j.RewardCents < freelance);                          // strictly below the freelance owner rate
        });
    }

    [Fact]
    public void ContractPay_StaysBelowOwnerMargin_EvenWithMaxLoyalty()
    {
        Assert.True(Cfg.ContractCarrierPayFactor < 1.0); // below the freelance owner rate...
        // ...and still under it after the maxed loyalty premium a carrier could ever pay — derived from the
        // config, not a magic number, so this guard (the ONLY runtime check on the below-margin invariant)
        // tracks any retune of ClientLoyaltyBonusMaxPct.
        double maxLoyaltyPremium = Cfg.ClientLoyaltyBonusPct(EconomyConfig.LoyaltyMax);
        Assert.True(Cfg.ContractCarrierPayFactor * (1.0 + maxLoyaltyPremium) < 1.0);
    }

    [Fact]
    public async Task Board_BrandsCarrierAsClient_AndWithholdsHubLift()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        Guid companyId;
        using (var db = tdb.NewContext())
        {
            db.Airports.AddRange(
                new Airport { Ident = "EHAM", IcaoCode = "EHAM", Name = "Schiphol", Latitude = 52.3086, Longitude = 4.7639, Kind = AirportKind.LargeAirport },
                new Airport { Ident = "EHRD", IcaoCode = "EHRD", Name = "Rotterdam", Latitude = 51.9569, Longitude = 4.4372, Kind = AirportKind.LargeAirport },
                new Airport { Ident = "EHEH", IcaoCode = "EHEH", Name = "Eindhoven", Latitude = 51.4501, Longitude = 5.3745, Kind = AirportKind.LargeAirport });
            await db.SaveChangesAsync();
        }
        using (var db = tdb.NewContext())
        {
            var company = new Company { Id = Guid.NewGuid(), Name = "Co", OperatingReputationMilli = 100_000 }; // a flawless name...
            db.Companies.Add(company);
            db.Bases.Add(new Base { Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = "EHAM", IsActive = true }); // ...at a hub
            await db.SaveChangesAsync();
            companyId = company.Id;
        }

        JobBoardService Board(CallsignDbContext db) => new(db, new AirportRepository(db), new ContractJobSource(Cfg), clock, Cfg, new WorldOracle(Cfg));
        using (var db = tdb.NewContext())
            await Board(db).RefreshAsync("EHAM", PilotRank.Trainee, count: 6, seed: 1, companyId);

        using (var db = tdb.NewContext())
        {
            var jobs = await Board(db).GetAvailableAsync("EHAM");
            Assert.NotEmpty(jobs);
            double demand = new WorldOracle(Cfg).EconomyPhaseAt(T0).DemandMult * Cfg.SeasonalDemandFactor(GameCalendar.Season(T0, 52.3086));
            Assert.All(jobs, j =>
            {
                Assert.StartsWith("contract:", j.ClientKey);                  // branded to the carrier as client
                Assert.False(string.IsNullOrEmpty(j.ClientName));
                // The carrier's fixed rate: contract base × macro demand, but NO hub-reputation lift even at a maxed hub.
                long contractBase = (long)Math.Round(Cfg.CargoRewardCents(j.DistanceNm, j.WeightLbs) * Cfg.ContractCarrierPayFactor);
                Assert.Equal((long)Math.Round(contractBase * demand), j.RewardCents);
            });
        }
    }
}
