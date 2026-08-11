using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;

namespace Callsign.Core.Game;

/// <summary>
/// Bootstraps a new career: the account (<see cref="Company"/>), the player <see cref="Pilot"/>,
/// and a starting balance posted through the ledger.
/// </summary>
public sealed class NewGameService
{
    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;

    public NewGameService(CallsignDbContext db, LedgerService ledger, IClock clock)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
    }

    public async Task<(Company Company, Pilot Pilot)> StartNewCareerAsync(
        string name, string homeIcao, decimal startingCash, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        var company = new Company { Id = Guid.NewGuid(), Name = name, UpdatedAt = now };
        _db.Companies.Add(company);

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Name = name,
            Rank = PilotRank.Trainee,
            HomeIcao = homeIcao,
            CurrentIcao = homeIcao,
            UpdatedAt = now,
        };
        _db.Pilots.Add(pilot);
        await _db.SaveChangesAsync(ct);

        // Even the opening balance is a ledger movement — nothing bypasses the ledger.
        await _ledger.PostAsync(company.Id, LedgerCategory.StartingBalance, startingCash,
            "New career starting balance", ct: ct);

        return (company, pilot);
    }
}
