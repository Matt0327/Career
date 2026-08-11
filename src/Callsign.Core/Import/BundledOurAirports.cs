using System.IO.Compression;

namespace Callsign.Core.Import;

/// <summary>
/// Opens the gzipped OurAirports snapshot embedded in this assembly. The data ships with the
/// app so it works offline out of the box and never asks the user to place a file
/// (brief §3.6); updates arrive over HTTP later and replace the database, not this snapshot.
/// </summary>
public static class BundledOurAirports
{
    public const string AirportsResource = "Callsign.Core.ourairports.airports.csv.gz";
    public const string RunwaysResource = "Callsign.Core.ourairports.runways.csv.gz";

    /// <summary>Decompressed airports.csv stream. Caller disposes.</summary>
    public static Stream OpenAirportsCsv() => OpenGz(AirportsResource);

    /// <summary>Decompressed runways.csv stream. Caller disposes.</summary>
    public static Stream OpenRunwaysCsv() => OpenGz(RunwaysResource);

    private static Stream OpenGz(string resource)
    {
        var asm = typeof(BundledOurAirports).Assembly;
        var raw = asm.GetManifestResourceStream(resource)
                  ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        return new GZipStream(raw, CompressionMode.Decompress);
    }
}
