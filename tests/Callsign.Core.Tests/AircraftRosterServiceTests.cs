using Callsign.Core.Aircraft;
using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

public class AircraftRosterServiceTests
{
    private static ScannedAircraftType Sample() => new(
        Key: "PC12",
        CanonicalName: "Pilatus PC-12/47",
        Manufacturer: "Pilatus",
        IcaoTypeDesignator: "PC12",
        IcaoModel: "PC12",
        Category: AircraftCategory.Turboprop,
        UiTypeRole: "Single Engine Turboprop",
        Titles: ["Pilatus PC-12/47 OH-JEM", "Pilatus PC-12/47 White"],
        Locations: [new ScannedLocation("Community2024", "sws-aircraft-pc12", "SWS_PC12_4B")]);

    [Fact]
    public async Task Replace_PersistsType_Aliases_AndLocalInstallState()
    {
        using var tdb = new TestDb();
        using (var db = tdb.NewContext())
            await new AircraftRosterService(db, new FakeClock()).ReplaceAsync([Sample()]);

        using (var db = tdb.NewContext())
        {
            var type = await db.AircraftTypes.Include(t => t.Aliases).SingleAsync();
            Assert.Equal("PC12", type.Key);
            Assert.Equal(AircraftCategory.Turboprop, type.Category);
            Assert.Equal(2, type.Aliases.Count);

            var normalized = type.Aliases.Select(a => a.TitleNormalized).ToList();
            Assert.Contains("pilatus pc 1247 oh jem", normalized); // title normalisation for §5.3 matching

            var installed = await db.InstalledPackages.SingleAsync();
            Assert.Equal(type.Id, installed.AircraftTypeId);
            Assert.Equal("Community2024", installed.Source);
            Assert.True(installed.IsOnDisk);
        }
    }

    [Fact]
    public async Task Rebuild_EnrichesScannedTypeWithCuratedSpecs_AndAddsStreamedDefaultFleet()
    {
        using var tdb = new TestDb();
        CuratedAircraft[] curated =
        [
            new("PC12", "Pilatus PC-12", "Pilatus", AircraftCategory.Turboprop, 9, 2100, 2704, 270, 2600, ["Pilatus PC-12"]),
            new("C172", "Cessna 172 Skyhawk", "Cessna", AircraftCategory.LightSingle, 4, 878, 336, 124, 1600, ["Cessna Skyhawk G1000 Asobo"]),
        ];

        using (var db = tdb.NewContext())
            await new AircraftRosterService(db, new FakeClock()).RebuildAsync([Sample()], curated);

        using (var db = tdb.NewContext())
        {
            // scanned PC-12 gains curated specs but stays on-disk
            var pc12 = await db.AircraftTypes.SingleAsync(t => t.Key == "PC12");
            Assert.Equal(270, pc12.CruiseKtas);
            Assert.Equal(9, pc12.Seats);
            var pc12Install = await db.InstalledPackages.SingleAsync(i => i.AircraftTypeId == pc12.Id);
            Assert.True(pc12Install.IsOnDisk);
            Assert.Equal("Community2024", pc12Install.Source);

            // curated-only C172 appears as the streamed default fleet
            var c172 = await db.AircraftTypes.SingleAsync(t => t.Key == "C172");
            Assert.Equal(124, c172.CruiseKtas);
            var c172Install = await db.InstalledPackages.SingleAsync(i => i.AircraftTypeId == c172.Id);
            Assert.False(c172Install.IsOnDisk);
            Assert.Equal("Default2024", c172Install.Source);
        }
    }

    [Fact]
    public async Task Replace_IsWholesale_OnRescan()
    {
        using var tdb = new TestDb();
        using (var db = tdb.NewContext())
            await new AircraftRosterService(db, new FakeClock()).ReplaceAsync([Sample()]);
        using (var db = tdb.NewContext())
            await new AircraftRosterService(db, new FakeClock()).ReplaceAsync([Sample()]); // rescan

        using (var db = tdb.NewContext())
        {
            Assert.Equal(1, await db.AircraftTypes.CountAsync());
            Assert.Equal(2, await db.AircraftTitleAliases.CountAsync());
            Assert.Equal(1, await db.InstalledPackages.CountAsync());
        }
    }
}
