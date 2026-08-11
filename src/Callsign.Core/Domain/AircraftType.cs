namespace Callsign.Core.Domain;

/// <summary>Our own broad category buckets (brief §2.4). Pinned — persisted as a string, but
/// values are fixed for safety if that ever changes.</summary>
public enum AircraftCategory
{
    Unknown = 0,
    LightSingle = 1,
    LightTwin = 2,
    Turboprop = 3,
    LightJet = 4,
    Jet = 5,
    Helicopter = 6,
    Glider = 7,
    Heavy = 8,
    Other = 9,
}

/// <summary>
/// A recognised aircraft type — shared reference identity (a PC-12 is a PC-12 on every machine).
/// It is built from the local scan but is conceptually global, so it carries NO machine-local scan
/// state (that lives on <see cref="InstalledPackage"/>). Physical specs are nullable: aircraft.cfg
/// identity is reliable, but seats/useful-load/fuel/cruise/runway come from a curated catalog added
/// later (brief §4 fallback).
/// </summary>
public sealed class AircraftType
{
    public Guid Id { get; set; }

    /// <summary>Stable dedup key: ICAO type designator when present, else a normalised name.</summary>
    public string Key { get; set; } = null!;

    public string CanonicalName { get; set; } = null!;
    public string? Manufacturer { get; set; }
    public string? IcaoTypeDesignator { get; set; }
    public string? IcaoModel { get; set; }
    public AircraftCategory Category { get; set; }
    public string? UiTypeRole { get; set; }

    // Physical specs — filled from a curated catalog later; null until then.
    public int? Seats { get; set; }
    public int? UsefulLoadLbs { get; set; }
    public int? FuelCapacityLbs { get; set; }
    public int? CruiseKtas { get; set; }
    public int? MinRunwayFt { get; set; }

    public List<AircraftTitleAlias> Aliases { get; set; } = new();
}

/// <summary>
/// An observed livery TITLE string that maps to an <see cref="AircraftType"/>. Used to reconcile the
/// SimConnect-reported title against the type without ever failing a flight over a mismatch (brief §5.3).
/// </summary>
public sealed class AircraftTitleAlias
{
    public long Id { get; set; }
    public Guid AircraftTypeId { get; set; }
    public string Title { get; set; } = null!;
    public string TitleNormalized { get; set; } = null!;
}
