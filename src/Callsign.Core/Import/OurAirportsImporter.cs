using System.Globalization;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Import;

public readonly record struct ImportResult(int Airports, int Runways);

/// <summary>
/// Imports the OurAirports airports + runways CSVs into the database. Robust to column
/// reordering/additions (maps by header name), derives each airport's longest non-closed
/// runway, and drops runways whose airport is not present.
/// </summary>
public sealed class OurAirportsImporter
{
    private readonly CallsignDbContext _db;

    public OurAirportsImporter(CallsignDbContext db) => _db = db;

    public async Task<ImportResult> ImportAsync(Stream airportsCsv, Stream runwaysCsv, CancellationToken ct = default)
    {
        var runways = ParseRunways(runwaysCsv);

        var longestByIdent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in runways)
        {
            if (r.Closed || r.LengthFt is not int len || len <= 0)
                continue;
            if (!longestByIdent.TryGetValue(r.AirportIdent, out var cur) || len > cur)
                longestByIdent[r.AirportIdent] = len;
        }

        var airports = ParseAirports(airportsCsv, longestByIdent);
        var idents = new HashSet<string>(airports.Select(a => a.Ident), StringComparer.OrdinalIgnoreCase);
        var keptRunways = runways.Where(r => idents.Contains(r.AirportIdent)).ToList();

        await BulkInsertAsync(airports, ct);   // airports first — runways FK-reference them
        await BulkInsertAsync(keptRunways, ct);
        return new ImportResult(airports.Count, keptRunways.Count);
    }

    private async Task BulkInsertAsync<T>(List<T> rows, CancellationToken ct) where T : class
    {
        var previous = _db.ChangeTracker.AutoDetectChangesEnabled;
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            const int batchSize = 5000;
            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var slice = rows.GetRange(i, Math.Min(batchSize, rows.Count - i));
                await _db.AddRangeAsync(slice, ct);
                await _db.SaveChangesAsync(ct);
                foreach (var e in slice)
                    _db.Entry(e).State = EntityState.Detached; // keep the change tracker small
            }
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = previous;
        }
    }

    private static List<Airport> ParseAirports(Stream csv, IReadOnlyDictionary<string, int> longestByIdent)
    {
        using var reader = new StreamReader(csv);
        using var rows = Csv.ReadRecords(reader).GetEnumerator();
        if (!rows.MoveNext())
            return new List<Airport>();

        var h = new Header(rows.Current);
        int iIdent = h["ident"], iType = h["type"], iName = h["name"],
            iLat = h["latitude_deg"], iLon = h["longitude_deg"], iElev = h["elevation_ft"],
            iCountry = h["iso_country"], iRegion = h["iso_region"], iMuni = h["municipality"],
            iSched = h["scheduled_service"], iIcao = h["icao_code"], iIata = h["iata_code"];

        var list = new List<Airport>();
        while (rows.MoveNext())
        {
            var f = rows.Current;
            var ident = Field(f, iIdent);
            if (string.IsNullOrWhiteSpace(ident))
                continue;

            var name = Field(f, iName);
            list.Add(new Airport
            {
                Ident = ident,
                IcaoCode = NullIfEmpty(Field(f, iIcao)),
                IataCode = NullIfEmpty(Field(f, iIata)),
                Kind = ParseKind(Field(f, iType)),
                Name = string.IsNullOrEmpty(name) ? ident : name,
                Latitude = ParseDouble(Field(f, iLat)),
                Longitude = ParseDouble(Field(f, iLon)),
                ElevationFt = ParseIntN(Field(f, iElev)),
                IsoCountry = NullIfEmpty(Field(f, iCountry)),
                IsoRegion = NullIfEmpty(Field(f, iRegion)),
                Municipality = NullIfEmpty(Field(f, iMuni)),
                ScheduledService = string.Equals(Field(f, iSched), "yes", StringComparison.OrdinalIgnoreCase),
                LongestRunwayFt = longestByIdent.TryGetValue(ident, out var lr) ? lr : (int?)null,
            });
        }
        return list;
    }

    private static List<Runway> ParseRunways(Stream csv)
    {
        using var reader = new StreamReader(csv);
        using var rows = Csv.ReadRecords(reader).GetEnumerator();
        if (!rows.MoveNext())
            return new List<Runway>();

        var h = new Header(rows.Current);
        int iIdent = h["airport_ident"], iLen = h["length_ft"], iWidth = h["width_ft"],
            iSurf = h["surface"], iLit = h["lighted"], iClosed = h["closed"],
            iLe = h["le_ident"], iHe = h["he_ident"];

        var list = new List<Runway>();
        while (rows.MoveNext())
        {
            var f = rows.Current;
            var ident = Field(f, iIdent);
            if (string.IsNullOrWhiteSpace(ident))
                continue;

            list.Add(new Runway
            {
                AirportIdent = ident,
                LengthFt = ParseIntN(Field(f, iLen)),
                WidthFt = ParseIntN(Field(f, iWidth)),
                Surface = NullIfEmpty(Field(f, iSurf)),
                Lighted = Field(f, iLit) == "1",
                Closed = Field(f, iClosed) == "1",
                LeIdent = NullIfEmpty(Field(f, iLe)),
                HeIdent = NullIfEmpty(Field(f, iHe)),
            });
        }
        return list;
    }

    private sealed class Header
    {
        private readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);

        public Header(string[] columns)
        {
            for (int i = 0; i < columns.Length; i++)
                _map[columns[i].Trim()] = i;
        }

        /// <summary>Column index by name, or -1 if the column is absent.</summary>
        public int this[string name] => _map.TryGetValue(name, out var i) ? i : -1;
    }

    private static string Field(string[] fields, int index)
        => (uint)index < (uint)fields.Length ? fields[index] : "";

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static double ParseDouble(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static int? ParseIntN(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;

    private static AirportKind ParseKind(string s) => s switch
    {
        "large_airport" => AirportKind.LargeAirport,
        "medium_airport" => AirportKind.MediumAirport,
        "small_airport" => AirportKind.SmallAirport,
        "heliport" => AirportKind.Heliport,
        "seaplane_base" => AirportKind.SeaplaneBase,
        "balloonport" => AirportKind.Balloonport,
        "closed" => AirportKind.Closed,
        _ => AirportKind.Unknown,
    };
}
