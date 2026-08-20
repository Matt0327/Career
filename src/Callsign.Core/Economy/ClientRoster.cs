namespace Callsign.Core.Economy;

/// <summary>One client slot from an origin's roster: a stable key and a display name.</summary>
public sealed record ClientSlot(string Key, string Name);

/// <summary>
/// The deterministic cast of clients that offer work out of an airport (Phase 8d). Pure function of the
/// origin — the same field always yields the same small roster of named businesses, so a client's stable
/// <see cref="ClientSlot.Key"/> maps back to its persisted loyalty row on every board refresh. Names are
/// hashed from the key (the same FNV-1a + fmix the market and weather use), mixing place-based names
/// ("Rotterdam Freight") with invented firms ("Meridian Logistics") so a board reads varied, not templated.
/// </summary>
public static class ClientRoster
{
    public const int DefaultSize = 4;

    private static readonly string[] PlaceSuffix =
    [
        "Freight", "Air Cargo", "Logistics", "Charters", "Air Services",
        "Express", "Aviation", "Air Transport", "Couriers", "Shipping",
    ];

    private static readonly string[] Prefix =
    [
        "Meridian", "Vanguard", "Cardinal", "Summit", "Harbor", "Ironline", "Northwind",
        "Delta", "Apex", "Keystone", "Silverline", "Anchor", "Pioneer", "Beacon", "Crestline", "Halcyon",
    ];

    private static readonly string[] Core =
    [
        "Logistics", "Freight", "Cargo", "Air", "Charters", "Holdings",
        "Trading Co.", "Group", "Couriers", "Air Freight", "Transport",
    ];

    /// <summary>The full roster for an origin — every client that can appear on its board.</summary>
    public static IReadOnlyList<ClientSlot> Roster(string originIcao, string? originName, int size = DefaultSize)
    {
        var list = new List<ClientSlot>(size);
        for (int s = 0; s < size; s++)
            list.Add(SlotAt(originIcao, originName, s));
        return list;
    }

    /// <summary>Which client offers the <paramref name="ordinal"/>-th generated job at this origin. Deterministic
    /// in (origin, ordinal), so a reproducible seed produces a reproducible client assignment.</summary>
    public static ClientSlot Pick(string originIcao, string? originName, int ordinal, int size = DefaultSize)
    {
        int slot = (int)(Hash($"{originIcao}|slot|{ordinal}") % (uint)Math.Max(1, size));
        return SlotAt(originIcao, originName, slot);
    }

    /// <summary>The client sitting in a fixed roster slot.</summary>
    public static ClientSlot SlotAt(string originIcao, string? originName, int slot)
    {
        string key = $"{originIcao}#{slot}";
        return new ClientSlot(key, NameFor(key, originName, originIcao));
    }

    private static string NameFor(string key, string? originName, string originIcao)
    {
        uint h = Hash(key);
        // Half the roster is place-named (grounds it in the field), half invented (keeps it varied).
        if ((h & 1) == 0)
        {
            string place = PlaceToken(originName, originIcao);
            return $"{place} {PlaceSuffix[(int)((h >> 1) % (uint)PlaceSuffix.Length)]}";
        }
        string prefix = Prefix[(int)((h >> 1) % (uint)Prefix.Length)];
        string core = Core[(int)((h >> 8) % (uint)Core.Length)];
        return $"{prefix} {core}";
    }

    // A short, human place token from the airport name — its first meaningful word ("Amsterdam Schiphol"
    // -> "Amsterdam"), falling back to the ICAO when there's no usable name.
    private static string PlaceToken(string? originName, string originIcao)
    {
        if (!string.IsNullOrWhiteSpace(originName))
        {
            var first = originName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (first.Length > 0 && first[0].Length >= 3)
                return first[0];
        }
        return originIcao;
    }

    // FNV-1a with a murmur3 fmix finalizer — the project's stable, well-avalanching hash.
    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return h;
    }
}
