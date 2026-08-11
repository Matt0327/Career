using Callsign.Core.Airports;
using Callsign.Core.Domain;
using Callsign.Core.Import;
using Xunit;

namespace Callsign.Core.Tests;

public class BundledOurAirportsTests
{
    // End-to-end: decompress the embedded snapshot, import it, and read a known airport back.
    [Fact]
    public async Task Bundle_Imports_And_Finds_Schiphol_WithRunways()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();

        var result = await new OurAirportsImporter(db)
            .ImportAsync(BundledOurAirports.OpenAirportsCsv(), BundledOurAirports.OpenRunwaysCsv());

        Assert.True(result.Airports > 80_000, $"airports={result.Airports}");
        Assert.True(result.Runways > 40_000, $"runways={result.Runways}");

        var repo = new AirportRepository(db);

        var eham = await repo.GetByIdentAsync("EHAM");
        Assert.NotNull(eham);
        Assert.Contains("Schiphol", eham!.Name);
        Assert.Equal(AirportKind.LargeAirport, eham.Kind);
        Assert.Equal(-11, eham.ElevationFt);
        Assert.Equal("AMS", eham.IataCode);
        Assert.True(eham.LongestRunwayFt >= 11_000, $"longest={eham.LongestRunwayFt}");

        var runways = await repo.GetRunwaysAsync("EHAM");
        Assert.True(runways.Count >= 5, $"runways={runways.Count}");

        var near = await repo.WithinRadiusAsync(eham.Latitude, eham.Longitude, 30);
        Assert.Contains(near, x => x.Airport.Ident == "EHAM");
    }
}
