using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Geo;
using Callsign.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Economy;

/// <summary>A hireable pilot preview. Its wage is economy-set (from a deterministic roll), never player-set.</summary>
public sealed record StaffCandidate(int Seed, string Name, long WagePerDayCents, int SkillMilli);

/// <summary>What a reconcile produced (for the reopen digest).</summary>
public sealed record ReconcileDigest(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long LoanCents, long NetCents);

/// <summary>
/// Staff + standing orders (Phase 2d): hire pilots, set repeating autonomous routes, and reconcile the
/// trips + wages that accrued while the app was closed. Everything moves through the ledger, so the
/// autonomous economy reconciles exactly like the hand-flown one.
/// </summary>
public sealed class OperationsService
{
    private static readonly string[] First = ["Amelia", "Jack", "Nadia", "Owen", "Priya", "Sven", "Lena", "Marco", "Yuki", "Diego", "Ingrid", "Tariq"];
    private static readonly string[] Last = ["Hart", "Boone", "Vance", "Okafor", "Rossi", "Lindqvist", "Nowak", "Haruna", "Silva", "Meyer", "Kaur", "Petrov"];

    private readonly CallsignDbContext _db;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private readonly EconomyConfig _cfg;

    public OperationsService(CallsignDbContext db, LedgerService ledger, IClock clock, EconomyConfig cfg)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock;
        _cfg = cfg;
    }

    /// <summary>A deterministic slate of hireable pilots for a seed.</summary>
    public IReadOnlyList<StaffCandidate> GenerateCandidates(int seed, int count = 5)
    {
        var rng = new Random(seed);
        var list = new List<StaffCandidate>(count);
        for (int i = 0; i < count; i++)
            list.Add(MakeCandidate(rng.Next()));
        return list;
    }

    private static StaffCandidate MakeCandidate(int seed)
    {
        var r = new Random(seed);
        var name = $"{First[r.Next(First.Length)]} {Last[r.Next(Last.Length)]}";
        int skill = 30_000 + r.Next(60_001);                 // 30%..90%
        long wage = 8_000 + skill / 1000L * 400;             // ~$200 (green) .. ~$440/day (ace)
        return new StaffCandidate(seed, name, wage, skill);
    }

    /// <summary>Hire the candidate identified by its seed (regenerated server-side, so the wage is trusted).</summary>
    public async Task<Staff> HireAsync(Guid companyId, int candidateSeed, CancellationToken ct = default)
    {
        var c = MakeCandidate(candidateSeed);
        var now = _clock.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(), CompanyId = companyId, Name = c.Name, Role = StaffRole.Pilot,
            WagePerDayCents = c.WagePerDayCents, SkillMilli = c.SkillMilli,
            HiredAt = now, LastPaidAt = now, IsActive = true, UpdatedAt = now,
        };
        _db.Staff.Add(staff);
        await _db.SaveChangesAsync(ct);
        return staff;
    }

    /// <summary>Assign a pilot + an owned aircraft to a repeating route (its reward frozen at economy price).</summary>
    public async Task<StandingOrder> CreateStandingOrderAsync(
        Guid companyId, Guid staffId, Guid aircraftId, string destIcao, CancellationToken ct = default)
    {
        var staff = await _db.Staff.FirstOrDefaultAsync(s => s.Id == staffId && s.CompanyId == companyId && s.IsActive, ct)
                    ?? throw new InvalidOperationException("Pilot not found.");
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == aircraftId && a.CompanyId == companyId, ct)
                       ?? throw new InvalidOperationException("Aircraft not found in your fleet.");
        if (aircraft.Availability != AircraftAvailability.Available)
            throw new InvalidOperationException("That aircraft isn't available.");

        var origin = aircraft.LocationIcao;
        var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == origin, ct)
                   ?? throw new InvalidOperationException($"Origin {origin} is unknown.");
        var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == destIcao, ct)
                   ?? throw new InvalidOperationException($"Destination {destIcao} is unknown.");

        double dist = GeoMath.DistanceNm(oAir.Latitude, oAir.Longitude, dAir.Latitude, dAir.Longitude);
        var type = await _db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == aircraft.TypeId, ct);
        double cruise = type?.CruiseKtas ?? 150;
        double rtHours = 2 * dist / Math.Max(60, cruise);
        int weight = Math.Min(type?.UsefulLoadLbs ?? 1_000, 1_000);
        long reward = _cfg.CargoRewardCents(dist, weight); // economy-frozen, never player-set

        var now = _clock.UtcNow;
        var order = new StandingOrder
        {
            Id = Guid.NewGuid(), CompanyId = companyId, StaffId = staffId, AircraftInstanceId = aircraftId,
            OriginIcao = origin, DestIcao = destIcao, DistanceNm = dist, RoundTripHours = rtHours,
            RewardPerTripCents = reward, Commodity = "General freight", WeightLbs = weight,
            IsActive = true, StartedAt = now, LastReconciledAt = now, UpdatedAt = now,
        };
        _db.StandingOrders.Add(order);
        aircraft.Availability = AircraftAvailability.Reserved; // held by the standing order
        aircraft.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Cancel a standing order and free its aircraft.</summary>
    public async Task CancelStandingOrderAsync(Guid companyId, Guid orderId, CancellationToken ct = default)
    {
        await ReconcileAsync(companyId, ct); // book any trips flown up to now first
        var order = await _db.StandingOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId, ct);
        if (order is null || !order.IsActive) return;
        order.IsActive = false;
        order.UpdatedAt = _clock.UtcNow;
        var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == order.AircraftInstanceId, ct);
        if (aircraft is not null) { aircraft.Availability = AircraftAvailability.Available; aircraft.UpdatedAt = _clock.UtcNow; }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Book the autonomous trips + wages that accrued since the last reconcile. Deterministic in the
    /// clock: N trips = floor(elapsed / round-trip time). Everything posts to the ledger, so cash always
    /// reconciles. Idempotent — the watermark only advances by whole booked trips.
    /// </summary>
    public async Task<ReconcileDigest> ReconcileAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        int totalTrips = 0;
        long grossIncome = 0, totalFees = 0, totalWages = 0, totalRent = 0, totalLoan = 0;

        // Airports where we own a base — landings there are fee-free.
        var baseIcaos = (await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .Select(b => b.AirportIcao).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var o in await _db.StandingOrders.Where(o => o.CompanyId == companyId && o.IsActive && !o.IsDeleted).ToListAsync(ct))
        {
            double elapsedH = (now - o.LastReconciledAt).TotalHours;
            int trips = o.RoundTripHours > 0 ? (int)Math.Floor(elapsedH / o.RoundTripHours) : 0;
            if (trips <= 0)
                continue;

            var oAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == o.OriginIcao, ct);
            var dAir = await _db.Airports.FirstOrDefaultAsync(a => a.Ident == o.DestIcao, ct);
            long feePerTrip = (oAir is not null && !baseIcaos.Contains(o.OriginIcao) ? _cfg.LandingFeeCents(oAir.Kind) : 0)
                            + (dAir is not null && !baseIcaos.Contains(o.DestIcao) ? _cfg.LandingFeeCents(dAir.Kind) : 0);
            long income = trips * o.RewardPerTripCents;
            long fees = trips * feePerTrip;
            var stamp = o.LastReconciledAt.UtcTicks;

            var postings = new List<LedgerPosting>
            {
                new(LedgerCategory.JobPayout, income / 100m, $"Standing order {o.OriginIcao}↔{o.DestIcao} ×{trips}",
                    AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}"),
            };
            if (fees > 0)
                postings.Add(new(LedgerCategory.AirportFee, -(fees / 100m), $"Landing fees {o.OriginIcao}↔{o.DestIcao} ×{trips}",
                    AircraftInstanceId: o.AircraftInstanceId, DedupeKey: $"so:{o.Id}:{stamp}:fee"));
            await _ledger.StageBatchAsync(companyId, postings, ct);

            var aircraft = await _db.AircraftInstances.FirstOrDefaultAsync(a => a.Id == o.AircraftInstanceId, ct);
            if (aircraft is not null)
            {
                double hours = trips * o.RoundTripHours;
                aircraft.AirframeHours += hours;
                int wear = (int)Math.Round(hours * _cfg.ConditionWearMilliPerHour);
                aircraft.HullConditionMilli = Math.Max(0, aircraft.HullConditionMilli - wear);
                aircraft.EngineConditionMilli = Math.Max(0, aircraft.EngineConditionMilli - wear);
                aircraft.UpdatedAt = now;
            }

            o.LastReconciledAt = o.LastReconciledAt.AddHours(trips * o.RoundTripHours); // advance by whole trips only
            o.UpdatedAt = now;
            totalTrips += trips;
            grossIncome += income;
            totalFees += fees;
        }

        foreach (var s in await _db.Staff.Where(s => s.CompanyId == companyId && s.IsActive && !s.IsDeleted).ToListAsync(ct))
        {
            double days = (now - s.LastPaidAt).TotalDays;
            long wage = (long)Math.Round(days * s.WagePerDayCents);
            if (wage <= 0)
                continue;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.StaffWage, -(wage / 100m), $"Wages — {s.Name}",
                    StaffId: s.Id, DedupeKey: $"wage:{s.Id}:{s.LastPaidAt.UtcTicks}"),
            }, ct);
            s.LastPaidAt = now;
            s.UpdatedAt = now;
            totalWages += wage;
        }

        foreach (var b in await _db.Bases.Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted).ToListAsync(ct))
        {
            double days = (now - b.LastRentBilledAt).TotalDays;
            long rent = (long)Math.Round(days * b.RentPerDayCents);
            if (rent <= 0)
                continue;
            await _ledger.StageBatchAsync(companyId, new[]
            {
                new LedgerPosting(LedgerCategory.BaseRent, -(rent / 100m), $"Base rent — {b.AirportIcao}",
                    BaseId: b.Id, DedupeKey: $"rent:{b.Id}:{b.LastRentBilledAt.UtcTicks}"),
            }, ct);
            b.LastRentBilledAt = now;
            b.UpdatedAt = now;
            totalRent += rent;
        }

        // Loans (Phase 4a): bill accrued interest + straight-line principal over whole elapsed days.
        foreach (var loan in await _db.Loans.Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active && !l.IsDeleted).ToListAsync(ct))
        {
            int days = (int)Math.Floor((now - loan.PaymentLastBilledAt).TotalDays);
            if (days <= 0)
                continue;
            var (interest, principal) = LoanCatalog.Amortize(loan.OutstandingCents, loan.PrincipalCents, loan.AprBps, loan.TermDays, days);
            var stamp = loan.PaymentLastBilledAt.UtcTicks;
            var lp = new List<LedgerPosting>();
            if (interest > 0)
                lp.Add(new(LedgerCategory.LoanInterest, -(interest / 100m), $"Loan interest — tier {loan.Tier}",
                    LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-int:{loan.Id}:{stamp}"));
            if (principal > 0)
                lp.Add(new(LedgerCategory.LoanPayment, -(principal / 100m), $"Loan repayment — tier {loan.Tier}",
                    LedgerRefType.Loan, loan.Id.ToString(), DedupeKey: $"loan-pay:{loan.Id}:{stamp}"));
            if (lp.Count > 0)
                await _ledger.StageBatchAsync(companyId, lp, ct);

            loan.OutstandingCents = Math.Max(0, loan.OutstandingCents - principal);
            loan.PaymentLastBilledAt = loan.PaymentLastBilledAt.AddDays(days); // advance by whole billed days only
            if (loan.OutstandingCents == 0)
                loan.Status = LoanStatus.PaidOff;
            loan.UpdatedAt = now;
            totalLoan += interest + principal;
        }

        await _db.SaveChangesAsync(ct);
        return new ReconcileDigest(totalTrips, grossIncome, totalFees, totalWages, totalRent, totalLoan,
            grossIncome - totalFees - totalWages - totalRent - totalLoan);
    }
}
