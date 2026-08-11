namespace Callsign.Core.Domain;

/// <summary>
/// A MACHINE-LOCAL record that an <see cref="AircraftType"/> is installed on THIS PC, and where it
/// came from. Never syncs and carries no sync hooks — installed-ness is a property of this machine,
/// not of the save or a shared catalog (the LAN companion phone has none; two PCs differ). This
/// separation is what keeps the shared type identity clean (foreclosure audit #4/#10).
/// </summary>
public sealed class InstalledPackage
{
    public Guid Id { get; set; }
    public Guid AircraftTypeId { get; set; }

    /// <summary>Which package tree it was found in, e.g. "Community2024", "Official2024".</summary>
    public string Source { get; set; } = null!;
    public string PackageFolder { get; set; } = null!;
    public string AircraftFolder { get; set; } = null!;

    /// <summary>True when a real aircraft.cfg was found on disk (vs. a streamed/curated default).</summary>
    public bool IsOnDisk { get; set; }
    public DateTimeOffset ScannedAt { get; set; }

    /// <summary>Reserved for multi-PC installs; null today.</summary>
    public Guid? HostClientId { get; set; }
}
