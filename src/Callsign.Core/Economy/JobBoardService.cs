using Callsign.Core.Airports;
using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Time;
using Callsign.Core.World;
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
    private readonly WorldOracle _world;

    public JobBoardService(
        CallsignDbContext db, AirportRepository airports, IJobSource source, IClock clock, EconomyConfig cfg, WorldOracle world)
    {
        _db = db;
        _airports = airports;
        _source = source;
        _clock = clock;
        _cfg = cfg;
        _world = world;
    }

    /// <summary>Regenerate the board at <paramref name="originIcao"/>; returns how many jobs were posted. When
    /// <paramref name="companyId"/> is a real company AND the origin is one of its active bases (a HUB), the
    /// airline's operating reputation (Phase 11c) widens the board and lifts each offer's pay, frozen here at
    /// posting. Default <c>Guid.Empty</c> (or an off-hub origin) is byte-identical to pre-11c: no lift, no
    /// company lookup.</summary>
    public async Task<int> RefreshAsync(
        string originIcao, PilotRank rank, int count, int seed, Guid companyId = default, CancellationToken ct = default)
    {
        var origin = await _airports.GetByIdentAsync(originIcao, ct)
                     ?? throw new InvalidOperationException($"Airport {originIcao} not found.");
        var now = _clock.UtcNow;

        // Phase 11c — is this board at one of MY bases? Only then does my name lift the demand. Off-hub (or no
        // company) short-circuits BEFORE the Company row is even read, so away boards price exactly as before.
        // Phase 11e — a base upgraded to a hub amplifies that lift by its HubLevel (0 = a plain base = 11c exactly).
        var homeBase = companyId == Guid.Empty ? null
            : await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted && b.AirportIcao == originIcao)
                .Select(b => new { b.HubLevel }).FirstOrDefaultAsync(ct);
        int repMilli = homeBase is not null
            ? await _db.Companies.Where(c => c.Id == companyId).Select(c => c.OperatingReputationMilli).FirstOrDefaultAsync(ct)
            : 0;
        int hubLevel = homeBase?.HubLevel ?? 0;
        double hubPay = _cfg.HubReputationPayFactor(repMilli, hubLevel); // 1.0 off-hub; amplified at an upgraded hub
        int effCount = homeBase is not null ? _cfg.HubReputationOfferCount(count, repMilli, hubLevel) : count;

        _db.Jobs.RemoveRange(await _db.Jobs.Where(j => j.OriginIcao == originIcao).ToListAsync(ct));

        var candidates = (await _airports.WithinRadiusAsync(origin.Latitude, origin.Longitude, _cfg.MaxJobDistanceNm, ct))
            .Where(x => x.Airport.Ident != originIcao && IsLandable(x.Airport) && HasRealIcao(x.Airport))
            .Select(x => new JobCandidate(x.Airport.Ident, x.DistanceNm))
            .ToList();

        var generated = _source.Generate(new JobGenerationRequest(originIcao, candidates, rank, effCount, seed));
        // The macro economy (Phase 8c) swings what clients pay: a boom lifts every offer, a bust discounts it.
        // A strong name at your hub (Phase 11c, hubPay) compounds on top — both frozen onto the job at posting,
        // so accepting locks the rate in and settlement reads it unchanged (pumping the name later owes nothing).
        // Phase 12 — the macro boom/bust cycle, tilted by the season at this field (world feels alive), both frozen
        // onto the offer at posting so an accepted job never re-rates.
        double demand = _world.EconomyPhaseAt(now).DemandMult * _cfg.SeasonalDemandFactor(GameCalendar.Season(now, origin.Latitude));
        for (int i = 0; i < generated.Count; i++)
        {
            var g = generated[i];
            // Whose job this is (Phase 8d): a client drawn deterministically from the origin's roster, so the
            // stable key maps back to their persisted loyalty when the leg settles. A contract job (Phase 11d) is
            // instead branded to its carrier as the client, and the carrier pays its OWN fixed rate — the hub-
            // reputation lift (11c) is withheld, since your name lifts your freelance work, not the majors'.
            string clientKey, clientName;
            double jobLift;
            if (g.ContractCarrier is { } carrier)
            {
                clientKey = $"contract:{carrier}";
                clientName = carrier;
                jobLift = 1.0;
            }
            else
            {
                var client = ClientRoster.Pick(originIcao, origin.Name, i);
                clientKey = client.Key;
                clientName = client.Name;
                jobLift = hubPay;
            }
            long reward = (long)Math.Round(g.RewardCents * demand * jobLift);
            // Phase 12 — the cents your name added at this hub (11c), frozen for the board to show. Already part of
            // the reward; this just makes the flywheel legible. Null off-hub / when the lift is neutral.
            long noLift = (long)Math.Round(g.RewardCents * demand);
            long? hubBonus = reward > noLift ? reward - noLift : null;
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
                RewardCents = reward,
                HubRepBonusCents = hubBonus,
                Xp = g.Xp,
                RequiredRank = g.RequiredRank,
                ClientKey = clientKey,
                ClientName = clientName,
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
