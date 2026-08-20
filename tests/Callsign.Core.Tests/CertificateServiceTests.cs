using Callsign.Core.Data;
using Callsign.Core.Domain;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 8e — operating certificates: the licences you earn (fee + standards bar), hold with an
/// expiry, and that gate premium categories of work.</summary>
public class CertificateServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ── the catalog ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequiredFor_MapsPremiumTypes_LeavesOpenWorkUngated()
    {
        Assert.Equal(CertificateKind.Charter, CertificateCatalog.RequiredFor(MissionType.Vip));
        Assert.Equal(CertificateKind.Hazmat, CertificateCatalog.RequiredFor(MissionType.Hazardous));
        Assert.Null(CertificateCatalog.RequiredFor(MissionType.Cargo));
        Assert.Null(CertificateCatalog.RequiredFor(MissionType.Passenger));
        Assert.Null(CertificateCatalog.RequiredFor(MissionType.Tourist)); // entry pax stays open
    }

    // ── seeding ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid CompanyId, Guid PilotId);

    private static async Task<Seed> SeedAsync(CallsignDbContext db, FakeClock clock,
        long cashCents = 20_000_000, int repMilli = 8_000, int settledFlights = 30, PilotRank rank = PilotRank.Captain)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = "Test Co" };
        var pilot = new Pilot { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Amelia", HomeIcao = "EHAM", CurrentIcao = "EHAM", ReputationMilli = repMilli, Rank = rank };
        db.Companies.Add(company); db.Pilots.Add(pilot);
        for (int i = 0; i < settledFlights; i++)
            db.JobAssignments.Add(new JobAssignment
            {
                Id = Guid.NewGuid(), JobId = Guid.NewGuid(), AccountId = company.Id, PilotId = pilot.Id,
                Type = MissionType.Cargo, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "x",
                Status = AssignmentStatus.Settled, AcceptedAt = T0, SettledAt = T0, RewardQuoteCents = 1000, XpQuote = 1,
            });
        await db.SaveChangesAsync();
        // Fund via the ledger so the cached balance is consistent (as the app does).
        await new LedgerService(db, clock).PostAsync(company.Id, LedgerCategory.StartingBalance, cashCents / 100m, "start");
        return new Seed(company.Id, pilot.Id);
    }

    private static CertificateService Svc(CallsignDbContext db, FakeClock clock) => new(db, new LedgerService(db, clock), clock);

    // ── applying ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_MeetsBar_IssuesCert_ChargesFee_SetsExpiry()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock);
        var svc = Svc(db, clock);

        var cert = await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter);

        var def = CertificateCatalog.Def(CertificateKind.Charter);
        Assert.Equal(CertificateKind.Charter, cert.Kind);
        Assert.Equal(T0.AddDays(def.ValidityDays), cert.ExpiresAt);
        Assert.True(cert.IsValidAt(T0));

        var company = await db.Companies.FirstAsync();
        Assert.Equal(20_000_000 - def.FeeCents, company.CashCents);
        Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.CertificateFee && e.AmountCents == -def.FeeCents);
    }

    [Theory]
    [InlineData(4_000, 30, 20_000_000)] // reputation below the bar
    [InlineData(8_000, 5, 20_000_000)]  // too few completed deliveries
    [InlineData(8_000, 30, 1_000_000)]  // can't afford the fee
    public async Task Apply_BelowStandards_Throws_AndIssuesNothing(int rep, int flights, long cash)
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock, cashCents: cash, repMilli: rep, settledFlights: flights);
        var svc = Svc(db, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(s.CompanyId, CertificateKind.Charter));
        Assert.Empty(await db.OperatingCertificates.ToListAsync());
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => e.Category == LedgerCategory.CertificateFee);
    }

    // ── authorisation + the gate ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authorization_FollowsAValidCertificate()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock);
        var svc = Svc(db, clock);

        var before = await svc.ValidKindsAsync(s.CompanyId, T0);
        Assert.False(CertificateService.Authorizes(before, MissionType.Vip));  // not yet certified
        Assert.True(CertificateService.Authorizes(before, MissionType.Cargo)); // ungated work always allowed

        await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter);

        var after = await svc.ValidKindsAsync(s.CompanyId, T0);
        Assert.True(CertificateService.Authorizes(after, MissionType.Vip));
        // once expired, authorisation lapses
        var lapsed = await svc.ValidKindsAsync(s.CompanyId, T0.AddDays(365));
        Assert.False(CertificateService.Authorizes(lapsed, MissionType.Vip));
    }

    [Fact]
    public async Task AcceptGate_RefusesVipWithoutCert_AllowsWithOne()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock);
        var job = new Job
        {
            Id = Guid.NewGuid(), Type = MissionType.Vip, OriginIcao = "EHAM", DestIcao = "EHRD", Commodity = "Executive",
            WeightLbs = 0, Pax = 3, RewardCents = 500_000, Xp = 40, DistanceNm = 120, RequiredRank = PilotRank.Captain,
            GeneratedAt = T0, ExpiresAt = T0.AddHours(6),
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        // No certificate -> the gate refuses (rank + rep already satisfied).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new JobAssignmentService(db, clock).AcceptAsync(job.Id, s.CompanyId, s.PilotId));
        Assert.Empty(await db.JobAssignments.Where(a => a.Type == MissionType.Vip).ToListAsync());

        // Grant a valid Charter certificate -> accept now succeeds.
        db.OperatingCertificates.Add(new OperatingCertificate
        {
            Id = Guid.NewGuid(), CompanyId = s.CompanyId, Kind = CertificateKind.Charter,
            IssuedAt = T0, ExpiresAt = T0.AddDays(120), UpdatedAt = T0,
        });
        await db.SaveChangesAsync();

        var a = await new JobAssignmentService(db, clock).AcceptAsync(job.Id, s.CompanyId, s.PilotId);
        Assert.Equal(MissionType.Vip, a.Type);
        Assert.Equal(AssignmentStatus.Accepted, a.Status);
    }

    // ── renewal + idempotency ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Renew_WhileValid_ExtendsFromCurrentExpiry_NoTimeLost()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock);
        var svc = Svc(db, clock);
        var def = CertificateCatalog.Def(CertificateKind.Charter);

        var c1 = await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter);
        Assert.Equal(T0.AddDays(def.ValidityDays), c1.ExpiresAt);

        var c2 = await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter); // renew, still valid
        Assert.Equal(T0.AddDays(def.ValidityDays * 2), c2.ExpiresAt);        // stacked, not reset to now+validity
        Assert.Equal(2, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CertificateFee));
        Assert.Single(await db.OperatingCertificates.ToListAsync());          // renewed in place, not duplicated
    }

    [Fact]
    public async Task Apply_IsIdempotent_UnderTheSameKey()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock);
        var svc = Svc(db, clock);

        await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter, idempotencyKey: "req-1");
        await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter, idempotencyKey: "req-1"); // a retry, not a second buy

        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CertificateFee));
        Assert.Single(await db.OperatingCertificates.ToListAsync());
    }

    [Fact]
    public async Task Apply_SameKey_DifferentKinds_DoNotCollide()
    {
        // Regression (8e review): the keyed dedupe must be namespaced by kind, or one Idempotency-Key reused
        // across two kinds mis-replays and blocks the second application.
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock); // clears both Charter and Hazmat (rep 8.0, 30 deliveries, ample cash)
        var svc = Svc(db, clock);

        await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter, idempotencyKey: "K");
        await svc.ApplyAsync(s.CompanyId, CertificateKind.Hazmat, idempotencyKey: "K"); // same key, different kind

        Assert.Equal(2, await db.OperatingCertificates.CountAsync());
        Assert.Equal(2, await db.LedgerEntries.CountAsync(e => e.Category == LedgerCategory.CertificateFee));
    }

    [Fact]
    public async Task GetStatus_ReportsHeldAndApplyableWithBar()
    {
        using var tdb = new TestDb();
        var clock = new FakeClock { UtcNow = T0 };
        using var db = tdb.NewContext();
        var s = await SeedAsync(db, clock, repMilli: 6_000, settledFlights: 20); // clears Charter, not Hazmat
        var svc = Svc(db, clock);
        await svc.ApplyAsync(s.CompanyId, CertificateKind.Charter);

        var status = await svc.GetStatusAsync(s.CompanyId);
        var charter = status.Single(x => x.Kind == CertificateKind.Charter);
        var hazmat = status.Single(x => x.Kind == CertificateKind.Hazmat);

        Assert.True(charter.Held && charter.Valid);
        Assert.Equal(120, charter.DaysLeft);
        Assert.False(hazmat.Held);
        Assert.False(hazmat.CanApply);           // rep 6.0 < 7.0 and 20 < 25 deliveries
        Assert.NotNull(hazmat.Blocker);
    }
}
