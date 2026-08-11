using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

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

        // Open the home base for free — it's where the fleet lives and lands fee-free.
        _db.Bases.Add(new Base
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, AirportIcao = homeIcao, IsHome = true,
            RentPerDayCents = 0, OpenedAt = now, LastRentBilledAt = now, IsActive = true, UpdatedAt = now,
        });

        // Grant a starter airframe so a fresh career can fly straight away (a gifted asset — the
        // ledger tracks CASH, and no cash moves for a gift; buying a real fleet is the Hangar).
        var starter = await _db.AircraftTypes
                          .Where(t => t.Category == AircraftCategory.LightSingle)
                          .OrderBy(t => t.CanonicalName).FirstOrDefaultAsync(ct)
                      ?? await _db.AircraftTypes.OrderBy(t => t.CanonicalName).FirstOrDefaultAsync(ct);
        if (starter is not null)
        {
            _db.AircraftInstances.Add(new AircraftInstance
            {
                Id = Guid.NewGuid(), TypeId = starter.Id, CompanyId = company.Id,
                Tail = "CS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                Ownership = OwnershipKind.Owned, Availability = AircraftAvailability.Available,
                LocationIcao = homeIcao, AcquiredAt = now, UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        return (company, pilot);
    }
}
