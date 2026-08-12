using Callsign.Core.Data;
using Callsign.Core.Progression;
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

/// <summary>A computed "reputation at scale": a tier + score derived on read, never stored.</summary>
public sealed record AirlineStanding(int Tier, string TierName, int Score, int? NextTierScore, IReadOnlyList<StandingContribution> Contributions);

/// <summary>
/// Airline identity (Phase 5c): the company's name, operator tail code, accent colour and emblem — set by
/// the player, with sensible derived defaults so there's always a look to show — plus a computed
/// <see cref="AirlineStanding"/> that reads the operation's reputation "at scale" (fleet, network, wealth,
/// campaigns and pilot reputation) into a named tier. Standing is a read model, like net worth.
/// </summary>
public sealed class AirlineService
{
    private static readonly string[] TierNames = { "Startup", "Regional", "National", "International", "Flag Carrier" };
    private static readonly int[] TierFloors = { 0, 40, 100, 200, 400 };
    private static readonly string[] Palette =
        { "#4f46e5", "#0e938d", "#c0362c", "#b8860b", "#2f6f4f", "#7a3ea8", "#c05621", "#1565c0" };

    private readonly CallsignDbContext _db;
    private readonly ProgressMetricsService _metrics;

    public AirlineService(CallsignDbContext db, ProgressMetricsService metrics)
    {
        _db = db;
        _metrics = metrics;
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

        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 60)
            throw new InvalidOperationException("An airline name of 1–60 characters is required.");

        var code = (tailCode ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 2 or > 3 || !code.All(char.IsLetter))
            throw new InvalidOperationException("The tail code must be 2–3 letters.");

        var color = (accentColorHex ?? "").Trim();
        if (!IsHexColor(color))
            throw new InvalidOperationException("The accent colour must be a hex value like #4f46e5.");

        if (!AirlineEmblems.IsValid(emblemKey))
            throw new InvalidOperationException($"Unknown emblem '{emblemKey}'.");

        company.AirlineName = trimmed;
        company.TailCode = code;
        company.AccentColorHex = color;
        company.EmblemKey = emblemKey;
        await _db.SaveChangesAsync(ct);
        return await GetIdentityAsync(companyId, ct);
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
            new("Fleet", m.Aircraft * 5),
            new("Bases", m.Bases * 8),
            new("Routes", m.Routes * 6),
            new("Campaigns completed", campaignsDone * 20),
            new("Achievements", achievements * 3),
            new("Net worth", netWorthPoints),
        };
        var score = contributions.Sum(c => c.Points);

        var tier = 0;
        for (var i = 0; i < TierFloors.Length; i++)
            if (score >= TierFloors[i]) tier = i;
        int? nextFloor = tier + 1 < TierFloors.Length ? TierFloors[tier + 1] : null;

        return new AirlineStanding(tier, TierNames[tier], score, nextFloor,
            contributions.Where(c => c.Points > 0).ToList());
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
