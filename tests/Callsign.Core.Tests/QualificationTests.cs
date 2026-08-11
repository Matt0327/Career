using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Game;
using Callsign.Core.Progression;
using Xunit;

namespace Callsign.Core.Tests;

public class QualificationTests
{
    [Theory]
    [InlineData(AircraftCategory.LightSingle, QualClass.A)]
    [InlineData(AircraftCategory.LightTwin, QualClass.B)]
    [InlineData(AircraftCategory.Turboprop, QualClass.C)]
    [InlineData(AircraftCategory.LightJet, QualClass.D)]
    [InlineData(AircraftCategory.Jet, QualClass.E)]
    [InlineData(AircraftCategory.Heavy, QualClass.F)]
    [InlineData(AircraftCategory.Helicopter, QualClass.H)]
    [InlineData(AircraftCategory.Glider, QualClass.M)]
    [InlineData(AircraftCategory.Other, QualClass.A)]     // unknown/other default to the base class
    [InlineData(AircraftCategory.Unknown, QualClass.A)]
    public void ForCategory_MapsCategoryToClass(AircraftCategory cat, QualClass expected)
        => Assert.Equal(expected, QualificationClasses.ForCategory(cat));

    [Fact]
    public void All_CoversEveryClass_AndStarterIsA()
    {
        Assert.Equal(Enum.GetValues<QualClass>().Length, QualificationClasses.All.Count);
        Assert.Equal(QualClass.A, QualificationClasses.Starter);
        Assert.All(QualificationClasses.All, d => Assert.False(string.IsNullOrWhiteSpace(d.Description))); // self-documenting
    }

    [Fact]
    public async Task NewCareer_GrantsStarterClassA_ButNotOthers()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock();
        Guid pilotId;
        using (var db = tdb.NewContext())
        {
            var (_, pilot) = await new NewGameService(db, new LedgerService(db, clock), clock)
                .StartNewCareerAsync("Amelia", "EHAM", 25_000m);
            pilotId = pilot.Id;
        }
        using (var db = tdb.NewContext())
        {
            var svc = new QualificationService(db);
            Assert.Contains(await svc.GetHeldAsync(pilotId), q => q.Class == QualClass.A);
            Assert.True(await svc.IsRatedAsync(pilotId, QualClass.A));
            Assert.False(await svc.IsRatedAsync(pilotId, QualClass.C)); // not rated for a turboprop yet
        }
    }
}
