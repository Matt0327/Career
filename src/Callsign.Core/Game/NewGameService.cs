using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;

namespace Callsign.Core.Game;

/// <summary>Bootstraps a new career: a pilot plus a starting balance posted through the ledger.</summary>
public sealed class NewGameService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;

    public NewGameService(CallsignDbContext db, LedgerService ledger)
    {
        _db = db;
        _ledger = ledger;
    }

    public async Task<Pilot> StartNewCareerAsync(
        string name, string homeIcao, decimal startingCash, CancellationToken ct = default)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            Name = name,
            Rank = PilotRank.Trainee,
            Xp = 0,
            CashCents = 0,
            HomeIcao = homeIcao,
            CurrentIcao = homeIcao,
            Reputation = 0,
        };

        _db.Pilots.Add(pilot);
        await _db.SaveChangesAsync(ct);

        // Even the opening balance is a ledger movement — nothing bypasses the ledger.
        await _ledger.PostAsync(pilot.Id, LedgerCategory.StartingBalance, startingCash,
            "New career starting balance", ct: ct);

        return pilot;
    }
}
