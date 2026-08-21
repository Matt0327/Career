using Callsign.Core.Aircraft;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Import;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Game;

/// <summary>
/// First-run bootstrap: makes sure the bundled airport database is imported and the aircraft roster
/// is built (scan + curated fleet), then starts a new career. Idempotent — importing only happens
/// once, and a rescan replaces the roster.
/// </summary>
public sealed class GameSetupService
{
    private readonly CallsignDbContext _db;
    private readonly AircraftScanner _scanner;
    private readonly AircraftRosterService _roster;
    private readonly NewGameService _newGame;

    public GameSetupService(CallsignDbContext db, AircraftScanner scanner, AircraftRosterService roster, NewGameService newGame)
    {
        _db = db;
        _scanner = scanner;
        _roster = roster;
        _newGame = newGame;
    }

    public async Task EnsureAirportsAsync(CancellationToken ct = default)
    {
        if (await _db.Airports.AnyAsync(ct))
            return;
        await new OurAirportsImporter(_db).ImportAsync(
            BundledOurAirports.OpenAirportsCsv(), BundledOurAirports.OpenRunwaysCsv(), ct);
    }

    public async Task RebuildRosterAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScannedAircraftType> scanned = MsfsInstallLocator.TryGetInstalledPackagesPath(out var ipp)
            ? _scanner.Scan(ipp)
            : Array.Empty<ScannedAircraftType>();
        await _roster.RebuildAsync(scanned, DefaultFleetCatalog.Aircraft2024, ct);
    }

    public async Task<(Company Company, Pilot Pilot)> StartNewCareerAsync(
        string name, string homeIcao, decimal startingCash,
        string? starterTypeCode = null, string? edition = null, string? avatarKey = null,
        CancellationToken ct = default)
    {
        await EnsureAirportsAsync(ct);
        await RebuildRosterAsync(ct);
        return await _newGame.StartNewCareerAsync(name, homeIcao, startingCash, starterTypeCode, edition, avatarKey, ct);
    }
}
