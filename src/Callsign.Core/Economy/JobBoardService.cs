using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>
/// The job board at an airport: refreshes the offers (generating fresh ones to nearby landable
/// airports) and lists the current, non-expired jobs.
/// </summary>
public sealed class JobBoardService
{
    private readonly CallsignDbContext _db;
    private readonly AirportRepository _airports;
    private readonly IJobSource _source;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public JobBoardService(
        CallsignDbContext db, AirportRepository airports, IJobSource source, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _airports = airports;
        _source = source;
        _clock = clock;
        _cfg = cfg;
    }

    /// <summary>Regenerate the board at <paramref name="originIcao"/>; returns how many jobs were posted.</summary>
    public async Task<int> RefreshAsync(
        string originIcao, PilotRank rank, int count, int seed, CancellationToken ct = default)
    {
        var origin = await _airports.GetByIdentAsync(originIcao, ct)
                     ?? throw new InvalidOperationException($"Airport {originIcao} not found.");
        var now = _clock.UtcNow;

        _db.Jobs.RemoveRange(await _db.Jobs.Where(j => j.OriginIcao == originIcao).ToListAsync(ct));

        var candidates = (await _airports.WithinRadiusAsync(origin.Latitude, origin.Longitude, _cfg.MaxJobDistanceNm, ct))
            .Where(x => x.Airport.Ident != originIcao && IsLandable(x.Airport) && HasRealIcao(x.Airport))
            .Select(x => new JobCandidate(x.Airport.Ident, x.DistanceNm))
            .ToList();

        var generated = _source.Generate(new JobGenerationRequest(originIcao, candidates, rank, count, seed));
        foreach (var g in generated)
        {
            _db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = g.Type,
                OriginIcao = g.OriginIcao,
                DestIcao = g.DestIcao,
                Commodity = g.Commodity,
                WeightLbs = g.WeightLbs,
                Pax = g.Pax,
                DistanceNm = g.DistanceNm,
                RewardCents = g.RewardCents,
                Xp = g.Xp,
                RequiredRank = g.RequiredRank,
                GeneratedAt = now,
                ExpiresAt = now.AddHours(_cfg.JobOfferHours),
                LoadByAt = now.AddHours(_cfg.JobOfferHours),
            });
        }

        await _db.SaveChangesAsync(ct);
        return generated.Count;
    }

    /// <summary>Current non-expired jobs at an airport, nearest first.</summary>
    public async Task<IReadOnlyList<Job>> GetAvailableAsync(string originIcao, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        return await _db.Jobs
            .Where(j => j.OriginIcao == originIcao && j.ExpiresAt > now)
            .OrderBy(j => j.DistanceNm)
            .ToListAsync(ct);
    }

    private static bool IsLandable(Airport a)
        => a.Kind is AirportKind.SmallAirport or AirportKind.MediumAirport or AirportKind.LargeAirport;

    // Only send pilots to airports whose IDENT is their ICAO code — i.e. real, sim-findable airports.
    // OurAirports uses placeholder idents ("NL-0029") for fields with no ICAO, and occasionally a
    // placeholder ident alongside a separate icao_code; requiring ident == icao excludes both.
    private static bool HasRealIcao(Airport a)
        => !string.IsNullOrWhiteSpace(a.IcaoCode) && string.Equals(a.Ident, a.IcaoCode, StringComparison.OrdinalIgnoreCase);
}
