using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Xunit;

namespace Callsign.Core.Tests;

public class DealerLocationsTests
{
    private static Airport Field(string ident, int? runwayFt, AirportKind kind = AirportKind.SmallAirport)
        => new() { Ident = ident, IcaoCode = ident, Kind = kind, Name = ident + " Field", LongestRunwayFt = runwayFt };

    // A pool of real, landable fields nearest-first (as WithinRadiusAsync returns).
    private static IReadOnlyList<Airport> Pool() =>
    [
        Field("EGAA", 8000, AirportKind.LargeAirport),
        Field("EGAC", 6000, AirportKind.MediumAirport),
        Field("EGAB", 3000),
        Field("EGAD", 2200),
        Field("EGAE", 1400),
    ];

    [Fact]
    public void Place_IsDeterministic()
    {
        var pool = Pool();
        string a = DealerLocations.Place("C172", 1600, pool, "ZZZZ");
        string b = DealerLocations.Place("C172", 1600, pool, "ZZZZ");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Place_OnlyPicksFieldsWithAdequateRunway()
    {
        var pool = Pool();
        // A heavy needing 6000 ft can only be placed at EGAA (8000) or EGAC (6000).
        var allowed = new[] { "EGAA", "EGAC" };
        foreach (var key in new[] { "B748", "A320", "GLF6", "seed-xyz", "42" })
            Assert.Contains(DealerLocations.Place(key, 6000, pool, "ZZZZ"), allowed);
    }

    [Fact]
    public void Place_DifferentAircraftSpreadAcrossFields()
    {
        var pool = Pool();
        // Light types (need little runway) should not all collapse onto one field.
        var picks = new HashSet<string>();
        foreach (var key in new[] { "C152", "C172", "P28A", "DR40", "SR22", "M20P", "BE58", "PC12" })
            picks.Add(DealerLocations.Place(key, 1200, pool, "ZZZZ"));
        Assert.True(picks.Count >= 2, "placement should spread stock across fields, not stack it at one");
    }

    [Fact]
    public void Place_FallsBackToBuyerFieldWhenNothingSuitable()
    {
        // No suitable field for a 12000 ft requirement → deliver to the buyer's own field (old behaviour).
        Assert.Equal("KJFK", DealerLocations.Place("A388", 12000, Pool(), "KJFK"));
        // Empty world data (e.g. an unseeded test DB) also falls back rather than throwing.
        Assert.Equal("KJFK", DealerLocations.Place("C172", 1600, System.Array.Empty<Airport>(), "KJFK"));
    }

    [Fact]
    public void Place_SkipsHeliportsAndClosedAndPlaceholderFields()
    {
        IReadOnlyList<Airport> pool =
        [
            new() { Ident = "H1", IcaoCode = "H1", Kind = AirportKind.Heliport, Name = "Heli", LongestRunwayFt = 9000 },
            new() { Ident = "XX-0001", IcaoCode = null, Kind = AirportKind.SmallAirport, Name = "Placeholder", LongestRunwayFt = 4000 },
            new() { Ident = "EGCL", IcaoCode = "EGCL", Kind = AirportKind.Closed, Name = "Closed", LongestRunwayFt = 5000 },
            Field("EGSS", 9000, AirportKind.LargeAirport),
        ];
        // Only EGSS is a real, landable field — every key must land there.
        foreach (var key in new[] { "C172", "B748", "x", "y" })
            Assert.Equal("EGSS", DealerLocations.Place(key, 1000, pool, "ZZZZ"));
    }
}
