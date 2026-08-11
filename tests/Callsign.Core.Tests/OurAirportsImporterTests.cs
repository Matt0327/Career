using System.Text;
using Callsign.Core.Airports;
using Callsign.Core.Domain;
using Callsign.Core.Import;
using Xunit;

namespace Callsign.Core.Tests;

public class OurAirportsImporterTests
{
    private const string AirportsCsv =
        "\"ident\",\"type\",\"name\",\"latitude_deg\",\"longitude_deg\",\"elevation_ft\",\"iso_country\",\"iso_region\",\"municipality\",\"scheduled_service\",\"icao_code\",\"iata_code\"\n" +
        "\"EHAM\",\"large_airport\",\"Amsterdam Airport Schiphol\",52.3086,4.7639,-11,\"NL\",\"NL-NH\",\"Amsterdam\",\"yes\",\"EHAM\",\"AMS\"\n" +
        "\"EHXX\",\"small_airport\",\"Test, Field \"\"Quoted\"\"\",52.0,5.0,15,\"NL\",\"NL-XX\",\"Testville\",\"no\",\"\",\"\"\n";

    private const string RunwaysCsv =
        "\"airport_ident\",\"length_ft\",\"width_ft\",\"surface\",\"lighted\",\"closed\",\"le_ident\",\"he_ident\"\n" +
        "\"EHAM\",6627,148,\"ASP\",1,0,\"04\",\"22\"\n" +
        "\"EHAM\",11329,148,\"ASP\",1,0,\"09\",\"27\"\n" +
        "\"EHAM\",5000,100,\"ASP\",1,1,\"18\",\"36\"\n" +
        "\"EHZZ\",3000,60,\"GRE\",0,0,\"12\",\"30\"\n";

    private static Stream S(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Import_MapsFields_DerivesLongest_FiltersOrphanRunways()
    {
        using var tdb = new TestDb();

        ImportResult result;
        using (var db = tdb.NewContext())
            result = await new OurAirportsImporter(db).ImportAsync(S(AirportsCsv), S(RunwaysCsv));

        Assert.Equal(2, result.Airports);
        Assert.Equal(3, result.Runways); // EHZZ runway dropped — no matching airport

        using (var db = tdb.NewContext())
        {
            var repo = new AirportRepository(db);

            var eham = await repo.GetByIdentAsync("EHAM");
            Assert.NotNull(eham);
            Assert.Equal(AirportKind.LargeAirport, eham!.Kind);
            Assert.Equal("Amsterdam Airport Schiphol", eham.Name);
            Assert.Equal(-11, eham.ElevationFt);
            Assert.Equal("AMS", eham.IataCode);
            Assert.True(eham.ScheduledService);
            Assert.Equal(11329, eham.LongestRunwayFt); // 5000 excluded (closed); 6627 < 11329
            Assert.Equal(3, (await repo.GetRunwaysAsync("EHAM")).Count);

            var ehxx = await repo.GetByIdentAsync("EHXX");
            Assert.NotNull(ehxx);
            Assert.Equal("Test, Field \"Quoted\"", ehxx!.Name); // comma + escaped quotes survive parsing
            Assert.False(ehxx.ScheduledService);
            Assert.Null(ehxx.IataCode);
            Assert.Null(ehxx.LongestRunwayFt);

            var near = await repo.WithinRadiusAsync(52.3086, 4.7639, 25);
            Assert.Contains(near, x => x.Airport.Ident == "EHAM");
        }
    }
}
