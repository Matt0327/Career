using System.ComponentModel.DataAnnotations.Schema;

namespace Callsign.Core.Domain;

/// <summary>Broad rank tiers (brief §2.1). Gates mission types as the game grows.</summary>
public enum PilotRank
{
    Trainee,
    Copilot,
    Captain,
    SeniorCaptain,
    Chief,
}

/// <summary>
/// The player's pilot. <see cref="CashCents"/> is a cached balance maintained only by
/// the ledger; it always equals the sum of the pilot's ledger entries.
/// </summary>
public sealed class Pilot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public PilotRank Rank { get; set; } = PilotRank.Trainee;
    public int Xp { get; set; }

    /// <summary>Cached balance in integer cents. Mutated only through the ledger.</summary>
    public long CashCents { get; set; }

    public string HomeIcao { get; set; } = null!;
    public string CurrentIcao { get; set; } = null!;
    public int Reputation { get; set; }

    /// <summary>Convenience view of <see cref="CashCents"/> in whole currency units.</summary>
    [NotMapped]
    public decimal Cash => CashCents / 100m;
}
