namespace Callsign.Server.Domain;

public enum ImageStatus { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>
/// A licensed/community image for an aircraft type, keyed by the stable <c>AircraftType.Key</c> (e.g.
/// "C172") so one photo serves every player's C172. Clean-room by construction: an image is served only
/// once <see cref="ImageStatus.Approved"/>, and <see cref="License"/> + <see cref="Attribution"/> are
/// mandatory on submission — no unlicensed or scraped imagery can enter the index.
/// </summary>
public sealed class AircraftImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/jpeg";
    public string Attribution { get; set; } = "";
    public string License { get; set; } = "";
    public string? SourceUrl { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public ImageStatus Status { get; set; } = ImageStatus.Pending;
    public int SortRank { get; set; }                 // higher wins among approved images for a key
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
