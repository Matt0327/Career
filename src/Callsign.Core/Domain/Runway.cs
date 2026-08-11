namespace Callsign.Core.Domain;

/// <summary>A runway from the OurAirports snapshot, linked to its <see cref="Airport"/> by ident.</summary>
public sealed class Runway
{
    public long Id { get; set; }
    public string AirportIdent { get; set; } = null!;
    public int? LengthFt { get; set; }
    public int? WidthFt { get; set; }
    public string? Surface { get; set; }
    public bool Lighted { get; set; }
    public bool Closed { get; set; }
    public string? LeIdent { get; set; }
    public string? HeIdent { get; set; }
}
