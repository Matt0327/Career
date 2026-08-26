using Callsign.Core.Data;
using Callsign.Core.Progression;
using Callsign.Core.Text;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Airline;

/// <summary>The fixed set of original emblem marks the UI can draw; the DB only stores the key.</summary>
public static class AirlineEmblems
{
    public static readonly IReadOnlyList<string> All = new[] { "roundel", "delta", "wing", "star", "compass", "peak" };
    public static bool IsValid(string? key) => key is not null && All.Contains(key);
}

/// <summary>The airline's look, with any unset field filled by a deterministic derived default.</summary>
public sealed record AirlineIdentity(string Name, string TailCode, string AccentColorHex, string EmblemKey, bool Customised);

public sealed record StandingContribution(string Label, int Points);

/// <summary>A computed "reputation at scale", derived on read, never stored (Phase 11b deepened it into the
/// career-stage journey). <see cref="Stage"/>/<see cref="StageName"/> are the named rung you stand on (the top
/// of the fully-met run); <see cref="Score"/> + <see cref="Contributions"/> are the informational operating
/// score (11a's math, unchanged); <see cref="Stages"/> is the whole 5-rung ladder with per-requirement
/// met/current/target; <see cref="NextMove"/> is the single binding next step (null at Flag Carrier).</summary>
public sealed record AirlineStanding(
    int Stage, string StageName, int Score, IReadOnlyList<StandingContribution> Contributions,
    IReadOnlyList<CareerStage> Stages, NextMove? NextMove);

/// <summary>One line on the Founder's Checklist (Phase 16a): a single prestige requirement, whether it's met, and
/// the player's current standing against the target — so the UI can render an honest, tick-as-you-go checklist.</summary>
public sealed record FounderRequirement(string Key, string Label, string Detail, bool Met, string Current, string Target);

/// <summary>Where the company stands on FORMING an airline. Phase 16a turned this from a rung-2 milestone into a hard
/// prestige grind: incorporating is the summit of the GA/charter career, gated behind a multi-requirement
/// <see cref="Requirements"/> checklist (wealth, hours, command rank, a real fleet, a proven operation, the AOC).
/// <see cref="Eligible"/> is true only when every requirement is met and you haven't incorporated yet.</summary>
public sealed record AirlineIncorporation(
    bool Incorporated, DateTimeOffset? IncorporatedAt,
    IReadOnlyList<FounderRequirement> Requirements, int MetCount, int TotalCount,
    bool Eligible, long FoundingFeeCents);

/// <summary>
/// Airline identity (Phase 5c): the company's name, operator tail code, accent colour and emblem — set by
/// the player, with sensible derived defaults so there's always a look to show — plus a computed
/// <see cref="AirlineStanding"/> that reads the operation's reputation "at scale" (fleet, network, wealth,
/// campaigns and pilot reputation) into a named tier. Standing is a read model, like net worth.
/// </summary>
public sealed class AirlineService
{
    private static readonly string[] Palette =
        { "#4f46e5", "#0e938d", "#c0362c", "#b8860b", "#2f6f4f", "#7a3ea8", "#c05621", "#1565c0" };

    private readonly CallsignDbContext _db;
    private readonly ProgressMetricsService _metrics;
    private readonly Callsign.Core.Economy.LedgerService? _ledger;   // Phase 13 — only needed to charge the founding fee
    private readonly Callsign.Core.Time.IClock? _clock;
    private readonly Callsign.Core.Economy.EconomyConfig? _cfg;

    public AirlineService(CallsignDbContext db, ProgressMetricsService metrics,
        Callsign.Core.Economy.LedgerService? ledger = null, Callsign.Core.Time.IClock? clock = null, Callsign.Core.Economy.EconomyConfig? cfg = null)
    {
        _db = db;
        _metrics = metrics;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    public async Task<AirlineIdentity> GetIdentityAsync(Guid companyId, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        var name = string.IsNullOrWhiteSpace(company.AirlineName) ? company.Name : company.AirlineName!;
        return new AirlineIdentity(
            name,
            company.TailCode ?? DeriveTailCode(name),
            company.AccentColorHex ?? Palette[(int)(StableHash(name) % (uint)Palette.Length)],
            company.EmblemKey ?? AirlineEmblems.All[(int)(StableHash(name + "·") % (uint)AirlineEmblems.All.Count)],
            Customised: !string.IsNullOrWhiteSpace(company.AirlineName));
    }

    public async Task<AirlineIdentity> SetIdentityAsync(
        Guid companyId, string? name, string? tailCode, string? accentColorHex, string? emblemKey, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");

        // Phase 13 — you can only name/re-brand once you've formally incorporated (or on a legacy save that already
        // had a name). Before that you're an Operator, and the identity is earned via IncorporateAsync.
        if (company.AirlineIncorporatedAt is null && string.IsNullOrWhiteSpace(company.AirlineName))
            throw new InvalidOperationException("Incorporate your airline first — you can't name it until then.");

        var (trimmed, code, color) = ValidateIdentity(name, tailCode, accentColorHex, emblemKey);
        company.AirlineName = trimmed;
        company.TailCode = code;
        company.AccentColorHex = color;
        company.EmblemKey = emblemKey;
        await _db.SaveChangesAsync(ct);
        return await GetIdentityAsync(companyId, ct);
    }

    private static (string Name, string Code, string Color) ValidateIdentity(string? name, string? tailCode, string? accentColorHex, string? emblemKey)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 60)
            throw new InvalidOperationException("An airline name of 1–60 characters is required.");
        NameGuard.Validate(trimmed, "airline name"); // Phase 12 — your airline is shown on the shared leaderboards

        var code = (tailCode ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 3 || !code.All(char.IsLetter))
            throw new InvalidOperationException("The tail code must be 2–3 letters.");

        var color = (accentColorHex ?? "").Trim();
        if (!IsHexColor(color))
            throw new InvalidOperationException("The accent colour must be a hex value like #4f46e5.");

        if (!AirlineEmblems.IsValid(emblemKey))
            throw new InvalidOperationException($"Unknown emblem '{emblemKey}'.");
        return (trimmed, code, color);
    }

    /// <summary>Where the company stands on incorporating (Phase 16a): whether it already has, and the full
    /// Founder's Checklist — a hard prestige grind of wealth, hours, command rank, fleet, a proven operation and the
    /// AOC. Eligible only when every requirement is met.</summary>
    public async Task<AirlineIncorporation> GetIncorporationStatusAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var cfg = _cfg ?? Callsign.Core.Economy.EconomyConfig.Default;
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        bool incorporated = company.AirlineIncorporatedAt is not null || !string.IsNullOrWhiteSpace(company.AirlineName); // legacy grandfather
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);
        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        bool hasAoc = await _db.OperatingCertificates.AnyAsync(
            c => c.CompanyId == companyId && c.Kind == Callsign.Core.Domain.CertificateKind.AirOperator && c.ExpiresAt > now, ct);

        static string Rank(int i) => i >= 0 && i < RankTiers.All.Count ? RankTiers.All[i].DisplayName : $"#{i}";
        var reqs = new List<FounderRequirement>
        {
            new("net_worth", "Build the balance sheet", "the net worth of a real operator",
                m.NetWorthCents >= cfg.FounderMinNetWorthCents,
                (m.NetWorthCents / 100m).ToString("C0"), (cfg.FounderMinNetWorthCents / 100m).ToString("C0")),
            new("flights", "Log the hours", "flights in the company book",
                m.Flights >= cfg.FounderMinFlights, $"{m.Flights}", $"{cfg.FounderMinFlights}"),
            new("rank", "Earn command", "a captain's rank or better",
                m.RankIndex >= cfg.FounderMinRankIndex, Rank(m.RankIndex), Rank(cfg.FounderMinRankIndex)),
            new("fleet", "Own a fleet", "aircraft on your certificate, not one tail",
                m.Aircraft >= cfg.FounderMinFleet, $"{m.Aircraft}", $"{cfg.FounderMinFleet}"),
            new("operating_rep", "Prove the operation", "operating reputation as a dependable carrier",
                m.OperatingReputationMilli >= cfg.FounderMinOperatingRepMilli,
                $"{m.OperatingReputationMilli / 1000}", $"{cfg.FounderMinOperatingRepMilli / 1000}"),
            new("aoc", "Hold an Air Operator Certificate", "the regulator's sign-off — earn it in Certificates",
                hasAoc, hasAoc ? "held" : "none", "valid AOC"),
        };
        int met = reqs.Count(r => r.Met);
        return new AirlineIncorporation(incorporated, company.AirlineIncorporatedAt, reqs, met, reqs.Count,
            !incorporated && met == reqs.Count, cfg.AirlineFoundingFeeCents);
    }

    /// <summary>Formally incorporate as an airline: validate the two gates + the identity, charge the founding fee,
    /// stamp the incorporation, and set the name/livery. One atomic step (Phase 13).</summary>
    public async Task<AirlineIdentity> IncorporateAsync(
        Guid companyId, Guid pilotId, string? name, string? tailCode, string? accentColorHex, string? emblemKey, CancellationToken ct = default)
    {
        var status = await GetIncorporationStatusAsync(companyId, pilotId, ct);
        if (status.Incorporated) throw new InvalidOperationException("Your airline is already incorporated.");
        if (!status.Eligible)
        {
            var unmet = status.Requirements.First(r => !r.Met);
            throw new InvalidOperationException(
                $"You're not ready to incorporate — {unmet.Label.ToLowerInvariant()}: {unmet.Current} of {unmet.Target}. " +
                $"({status.MetCount} of {status.TotalCount} founder requirements met.)");
        }
        if (_ledger is null || _clock is null) throw new InvalidOperationException("Incorporation isn't available here.");

        var (trimmed, code, color) = ValidateIdentity(name, tailCode, accentColorHex, emblemKey);
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct)
                      ?? throw new InvalidOperationException($"Company {companyId} not found.");
        long fee = status.FoundingFeeCents;
        if (company.CashCents < fee)
            throw new InvalidOperationException($"Founding an airline costs {fee / 100m:C0} — you have {company.Cash:C0}.");

        var now = _clock.UtcNow;
        await _ledger.StageBatchAsync(companyId, new[]
        {
            new Callsign.Core.Economy.LedgerPosting(Callsign.Core.Domain.LedgerCategory.AirlineFounding, -(fee / 100m),
                $"Airline incorporation — {trimmed}", DedupeKey: $"incorporate:{companyId}"),
        }, ct);
        company.AirlineName = trimmed;
        company.TailCode = code;
        company.AccentColorHex = color;
        company.EmblemKey = emblemKey;
        company.AirlineIncorporatedAt = now;
        company.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return await GetIdentityAsync(companyId, ct);
    }

    /// <summary>"The Flotation" (Phase 13): read the airline's enterprise value as a share price, and — for an
    /// incorporated airline — mark a fresh value snapshot into the history at most once per configured interval,
    /// pruning the tail. The returned history is the share-price ticker. Money-neutral (no ledger), gates nothing.</summary>
    public async Task<AirlineMarket.Readout> GetMarketAsync(Guid companyId, long valuationCents, bool incorporated, CancellationToken ct = default)
    {
        var cfg = _cfg ?? Callsign.Core.Economy.EconomyConfig.Default;
        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;

        if (incorporated)
        {
            var last = await _db.AirlineValueSnapshots.Where(s => s.CompanyId == companyId)
                .OrderByDescending(s => s.AtUtc).FirstOrDefaultAsync(ct);
            if (last is null || (now - last.AtUtc).TotalHours >= cfg.AirlineValueSnapshotIntervalHours)
            {
                _db.AirlineValueSnapshots.Add(new Callsign.Core.Domain.AirlineValueSnapshot
                {
                    CompanyId = companyId, AtUtc = now, ValuationCents = valuationCents,
                });
                await _db.SaveChangesAsync(ct);

                // Keep only the most recent window of marks (the ticker), so the table can't grow without bound.
                int count = await _db.AirlineValueSnapshots.CountAsync(s => s.CompanyId == companyId, ct);
                if (count > cfg.AirlineValueHistoryMax)
                {
                    var stale = await _db.AirlineValueSnapshots.Where(s => s.CompanyId == companyId)
                        .OrderByDescending(s => s.AtUtc).Skip(cfg.AirlineValueHistoryMax).ToListAsync(ct);
                    _db.AirlineValueSnapshots.RemoveRange(stale);
                    await _db.SaveChangesAsync(ct);
                }
            }
        }

        var rows = await _db.AirlineValueSnapshots.Where(s => s.CompanyId == companyId)
            .OrderBy(s => s.AtUtc).ToListAsync(ct);
        var history = rows.Select(r => (r.AtUtc, r.ValuationCents)).ToList();
        return AirlineMarket.Compute(cfg, valuationCents, history);
    }

    public async Task<AirlineStanding> GetStandingAsync(Guid companyId, Guid pilotId, CancellationToken ct = default)
    {
        var m = await _metrics.SnapshotAsync(companyId, pilotId, ct);
        var campaignsDone = await _db.CampaignProgress.CountAsync(c => c.CompanyId == companyId && c.CompletedAt != null && !c.IsDeleted, ct);
        var achievements = await _db.AchievementAwards.CountAsync(a => a.CompanyId == companyId && !a.IsDeleted, ct);

        // A legible score: each lever contributes visible points, shown as a breakdown in the UI.
        var netWorthDollars = m.NetWorthCents / 100.0;
        var netWorthPoints = Math.Max(0, (int)Math.Round((Math.Log10(Math.Max(1, netWorthDollars)) - 2) * 8));
        var contributions = new List<StandingContribution>
        {
            new("Pilot reputation", m.ReputationMilli / 1000),
            // Phase 11a — the airline's own operating reputation is now a real lever (was always 0 before this
            // engine existed), at parity weight with pilot reputation. A fresh company reads 0 and the
            // "hide zero levers" filter below drops it, so seeded-at-0 tests are unchanged.
            new("Airline reputation", m.OperatingReputationMilli / 1000),
            new("Fleet", m.Aircraft * 5),
            new("Bases", m.Bases * 8),
            new("Routes", m.Routes * 6),
            new("Campaigns completed", campaignsDone * 20),
            new("Achievements", achievements * 3),
            new("Net worth", netWorthPoints),
        };
        var score = contributions.Sum(c => c.Points);

        // Phase 11b — the stage journey, over the SAME snapshot m already read (no new query). The score above is
        // now informational; the named rung comes from the multi-requirement ladder, subsuming the old tier floors.
        var (stage, stages, move) = CareerLadder.Evaluate(m);

        return new AirlineStanding(stage, stages[stage].Name, score,
            contributions.Where(c => c.Points > 0).ToList(), stages, move);
    }

    // First letter of up to three words, else the first letters of a single word — always 2–3 letters.
    private static string DeriveTailCode(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length >= 2
            ? new string(words.Take(3).Select(w => w[0]).ToArray())
            : new string(name.Where(char.IsLetter).Take(3).ToArray());
        letters = new string(letters.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters.Length >= 2 ? letters : (letters + "XX")[..2];
    }

    private static bool IsHexColor(string s) =>
        (s.Length == 7) && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    // FNV-1a — deterministic across runs (unlike string.GetHashCode), so a name always derives the same look.
    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 16777619;
        }
        return h;
    }
}
