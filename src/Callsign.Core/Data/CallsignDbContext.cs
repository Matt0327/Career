using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Callsign.Core.Data;

/// <summary>The SQLite-backed database for a single save. One file, easy backup (brief §5.1).</summary>
public sealed class CallsignDbContext : DbContext
{
    public CallsignDbContext(DbContextOptions<CallsignDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Pilot> Pilots => Set<Pilot>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Airport> Airports => Set<Airport>();
    public DbSet<Runway> Runways => Set<Runway>();
    public DbSet<AircraftType> AircraftTypes => Set<AircraftType>();
    public DbSet<AircraftTitleAlias> AircraftTitleAliases => Set<AircraftTitleAlias>();
    public DbSet<InstalledPackage> InstalledPackages => Set<InstalledPackage>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAssignment> JobAssignments => Set<JobAssignment>();
    public DbSet<Callsign.Core.Domain.Flight> Flights => Set<Callsign.Core.Domain.Flight>();
    public DbSet<FlightEventRecord> FlightEvents => Set<FlightEventRecord>();
    public DbSet<AircraftInstance> AircraftInstances => Set<AircraftInstance>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Executive> Executives => Set<Executive>();
    public DbSet<StandingOrder> StandingOrders => Set<StandingOrder>();
    public DbSet<DispatchLeg> DispatchLegs => Set<DispatchLeg>();
    public DbSet<Base> Bases => Set<Base>();
    public DbSet<InventoryLot> InventoryLots => Set<InventoryLot>();
    public DbSet<MarketPressure> MarketPressures => Set<MarketPressure>();
    public DbSet<PilotQualification> PilotQualifications => Set<PilotQualification>();
    public DbSet<ReputationEvent> ReputationEvents => Set<ReputationEvent>();
    public DbSet<AirlineReputationEvent> AirlineReputationEvents => Set<AirlineReputationEvent>();
    public DbSet<AirlineValueSnapshot> AirlineValueSnapshots => Set<AirlineValueSnapshot>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<InsurancePolicy> InsurancePolicies => Set<InsurancePolicy>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<AchievementAward> AchievementAwards => Set<AchievementAward>();
    public DbSet<CampaignProgress> CampaignProgress => Set<CampaignProgress>();
    public DbSet<ChallengeProgress> ChallengeProgress => Set<ChallengeProgress>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<OperatingCertificate> OperatingCertificates => Set<OperatingCertificate>();
    public DbSet<RentalAgreement> RentalAgreements => Set<RentalAgreement>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native DateTimeOffset; store it as a chronologically-sortable long so that
        // comparisons (job expiry, later offline-billing windows) translate to SQL.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        var company = model.Entity<Company>();
        company.HasKey(c => c.Id);
        company.Property(c => c.Name).IsRequired().HasMaxLength(120);
        company.Property(c => c.AirlineName).HasMaxLength(60);
        company.Property(c => c.TailCode).HasMaxLength(3);
        company.Property(c => c.AccentColorHex).HasMaxLength(9);
        company.Property(c => c.EmblemKey).HasMaxLength(20);
        company.Property(c => c.Version).IsConcurrencyToken(); // races on the cash cache conflict, not clobber

        var pilot = model.Entity<Pilot>();
        pilot.HasKey(p => p.Id);
        pilot.Property(p => p.Name).IsRequired().HasMaxLength(80);
        pilot.Property(p => p.HomeIcao).IsRequired().HasMaxLength(8);
        pilot.Property(p => p.CurrentIcao).IsRequired().HasMaxLength(8);
        pilot.HasIndex(p => p.CompanyId);
        pilot.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var ledger = model.Entity<LedgerEntry>();
        ledger.HasKey(e => e.Id);
        ledger.Property(e => e.Id).ValueGeneratedOnAdd();
        ledger.Property(e => e.Category).HasConversion<string>().HasMaxLength(40);
        ledger.Property(e => e.Description).IsRequired().HasMaxLength(200);
        ledger.Property(e => e.RelatedEntityId).HasMaxLength(80);
        ledger.Property(e => e.DedupeKey).HasMaxLength(120);
        ledger.HasIndex(e => e.EntryUid).IsUnique();
        ledger.HasIndex(e => new { e.AccountId, e.DedupeKey }).IsUnique();
        ledger.HasIndex(e => new { e.AccountId, e.At });
        ledger.HasIndex(e => new { e.AircraftInstanceId, e.At });
        ledger.HasIndex(e => new { e.StaffId, e.At });
        ledger.HasIndex(e => new { e.BaseId, e.At });
        ledger.HasIndex(e => new { e.RelatedEntityType, e.RelatedEntityId });
        ledger.HasOne<Company>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);

        // --- Reference data: bundled OurAirports snapshot, replaced wholesale on update ---
        var airport = model.Entity<Airport>();
        airport.HasKey(a => a.Ident);
        airport.Property(a => a.Ident).HasMaxLength(12);
        airport.Property(a => a.IcaoCode).HasMaxLength(12);
        airport.Property(a => a.IataCode).HasMaxLength(4);
        airport.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
        airport.Property(a => a.Name).IsRequired().HasMaxLength(200);
        airport.Property(a => a.IsoCountry).HasMaxLength(4);
        airport.Property(a => a.IsoRegion).HasMaxLength(8);
        airport.Property(a => a.Municipality).HasMaxLength(120);
        airport.HasIndex(a => a.IcaoCode);
        airport.HasIndex(a => a.IsoCountry);
        airport.HasIndex(a => new { a.Latitude, a.Longitude });

        var runway = model.Entity<Runway>();
        runway.HasKey(r => r.Id);
        runway.Property(r => r.Id).ValueGeneratedOnAdd();
        runway.Property(r => r.AirportIdent).IsRequired().HasMaxLength(12);
        runway.Property(r => r.Surface).HasMaxLength(40);
        runway.Property(r => r.LeIdent).HasMaxLength(8);
        runway.Property(r => r.HeIdent).HasMaxLength(8);
        runway.HasIndex(r => r.AirportIdent);
        runway.HasOne<Airport>().WithMany().HasForeignKey(r => r.AirportIdent).OnDelete(DeleteBehavior.Cascade);

        // --- Aircraft: shared type identity + machine-local install state (foreclosure audit #10) ---
        var type = model.Entity<AircraftType>();
        type.HasKey(a => a.Id);
        type.HasIndex(a => a.Key).IsUnique();
        type.Property(a => a.Key).IsRequired().HasMaxLength(80);
        type.Property(a => a.CanonicalName).IsRequired().HasMaxLength(160);
        type.Property(a => a.Manufacturer).HasMaxLength(80);
        type.Property(a => a.IcaoTypeDesignator).HasMaxLength(12);
        type.Property(a => a.IcaoModel).HasMaxLength(40);
        type.Property(a => a.Category).HasConversion<string>().HasMaxLength(20);
        type.Property(a => a.UiTypeRole).HasMaxLength(60);
        type.HasMany(a => a.Aliases).WithOne().HasForeignKey(x => x.AircraftTypeId).OnDelete(DeleteBehavior.Cascade);

        var alias = model.Entity<AircraftTitleAlias>();
        alias.HasKey(x => x.Id);
        alias.Property(x => x.Id).ValueGeneratedOnAdd();
        alias.Property(x => x.Title).IsRequired().HasMaxLength(200);
        alias.Property(x => x.TitleNormalized).IsRequired().HasMaxLength(200);
        alias.HasIndex(x => x.TitleNormalized);

        var installed = model.Entity<InstalledPackage>();
        installed.HasKey(x => x.Id);
        installed.Property(x => x.Source).IsRequired().HasMaxLength(40);
        installed.Property(x => x.PackageFolder).IsRequired().HasMaxLength(200);
        installed.Property(x => x.AircraftFolder).IsRequired().HasMaxLength(200);
        installed.HasIndex(x => x.AircraftTypeId);
        installed.HasOne<AircraftType>().WithMany().HasForeignKey(x => x.AircraftTypeId).OnDelete(DeleteBehavior.Cascade);

        // --- Generated freelance job offers ---
        var job = model.Entity<Job>();
        job.HasKey(j => j.Id);
        job.Property(j => j.Type).HasConversion<string>().HasMaxLength(20);
        job.Property(j => j.OriginIcao).IsRequired().HasMaxLength(12);
        job.Property(j => j.DestIcao).IsRequired().HasMaxLength(12);
        job.Property(j => j.Commodity).IsRequired().HasMaxLength(60);
        job.Property(j => j.ClientKey).HasMaxLength(40);
        job.Property(j => j.ClientName).HasMaxLength(80);
        job.HasIndex(j => new { j.OriginIcao, j.ExpiresAt });

        // --- Accepted jobs (frozen quote) and settled flights ---
        var assignment = model.Entity<JobAssignment>();
        assignment.HasKey(x => x.Id);
        assignment.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        assignment.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        assignment.Property(x => x.OriginIcao).IsRequired().HasMaxLength(12);
        assignment.Property(x => x.DestIcao).IsRequired().HasMaxLength(12);
        assignment.Property(x => x.Commodity).IsRequired().HasMaxLength(60);
        assignment.Property(x => x.ClientKey).HasMaxLength(40);
        assignment.Property(x => x.ClientName).HasMaxLength(80);
        assignment.HasIndex(x => x.AccountId);
        assignment.HasIndex(x => x.JobId);

        var flight = model.Entity<Callsign.Core.Domain.Flight>();
        flight.HasKey(f => f.Id);
        flight.Property(f => f.AircraftTitle).IsRequired().HasMaxLength(200);
        flight.Property(f => f.PayoutBreakdownJson).IsRequired();
        flight.HasIndex(f => f.FlownByPilotId);
        flight.HasIndex(f => f.AircraftInstanceId);
        flight.HasIndex(f => f.JobAssignmentId).IsUnique(); // one settled flight per assignment (store-level double-settle guard)

        // --- Scored flight events (Phase 7a): the real tracker events, persisted for logbook replay ---
        var fev = model.Entity<FlightEventRecord>();
        fev.HasKey(e => e.Id);
        fev.Property(e => e.Severity).IsRequired().HasMaxLength(16);
        fev.Property(e => e.Message).IsRequired().HasMaxLength(200);
        fev.HasIndex(e => new { e.FlightId, e.Seq }); // fetch one flight's events in order

        // --- Owned airframes (Phase 2a) ---
        var instance = model.Entity<AircraftInstance>();
        instance.HasKey(a => a.Id);
        instance.Property(a => a.Tail).IsRequired().HasMaxLength(12);
        instance.Property(a => a.Ownership).HasConversion<string>().HasMaxLength(16);
        instance.Property(a => a.Availability).HasConversion<string>().HasMaxLength(16);
        instance.Property(a => a.LocationIcao).IsRequired().HasMaxLength(12);
        instance.HasIndex(a => a.CompanyId);
        instance.HasOne<AircraftType>().WithMany().HasForeignKey(a => a.TypeId).OnDelete(DeleteBehavior.Restrict);
        instance.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Staff + standing orders (Phase 2d) ---
        var staff = model.Entity<Staff>();
        staff.HasKey(s => s.Id);
        staff.Property(s => s.Name).IsRequired().HasMaxLength(80);
        staff.Property(s => s.Role).HasConversion<string>().HasMaxLength(16);
        staff.Property(s => s.CurrentIcao).HasMaxLength(12); // Phase 12 — soft FK to Airports.Ident; nullable = un-positioned
        staff.HasIndex(s => s.CompanyId);
        staff.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // Phase 16c — the executive suite: senior company-level hires, one per role.
        var exec = model.Entity<Executive>();
        exec.HasKey(e => e.Id);
        exec.Property(e => e.Name).IsRequired().HasMaxLength(80);
        exec.Property(e => e.Role).HasConversion<string>().HasMaxLength(24);
        exec.HasIndex(e => e.CompanyId);
        exec.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var order = model.Entity<StandingOrder>();
        order.HasKey(o => o.Id);
        order.Property(o => o.OriginIcao).IsRequired().HasMaxLength(12);
        order.Property(o => o.DestIcao).IsRequired().HasMaxLength(12);
        order.Property(o => o.Commodity).IsRequired().HasMaxLength(60);
        order.Property(o => o.PriceMultiplierMilli).HasDefaultValue(1000); // existing lines backfill to the fair rate
        order.HasIndex(o => o.CompanyId);
        order.HasOne<Company>().WithMany().HasForeignKey(o => o.CompanyId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<Staff>().WithMany().HasForeignKey(o => o.StaffId).OnDelete(DeleteBehavior.Restrict);
        order.HasOne<AircraftInstance>().WithMany().HasForeignKey(o => o.AircraftInstanceId).OnDelete(DeleteBehavior.Restrict);

        // Phase 12 — a dispatched one-off crew leg (a board job flown autonomously, one-way).
        var dispatch = model.Entity<DispatchLeg>();
        dispatch.HasKey(d => d.Id);
        dispatch.Property(d => d.OriginIcao).IsRequired().HasMaxLength(12);
        dispatch.Property(d => d.DestIcao).IsRequired().HasMaxLength(12);
        dispatch.Property(d => d.Commodity).HasMaxLength(60);
        dispatch.Property(d => d.ClientKey).HasMaxLength(80);
        dispatch.Property(d => d.ClientName).HasMaxLength(80);
        dispatch.Property(d => d.Type).HasConversion<string>().HasMaxLength(24);
        dispatch.Property(d => d.Status).HasConversion<string>().HasMaxLength(12);
        dispatch.HasIndex(d => d.CompanyId);
        dispatch.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
        dispatch.HasOne<Staff>().WithMany().HasForeignKey(d => d.StaffId).OnDelete(DeleteBehavior.Restrict);
        dispatch.HasOne<AircraftInstance>().WithMany().HasForeignKey(d => d.AircraftInstanceId).OnDelete(DeleteBehavior.Restrict);

        var companyBase = model.Entity<Base>();
        companyBase.HasKey(b => b.Id);
        companyBase.Property(b => b.AirportIcao).IsRequired().HasMaxLength(12);
        companyBase.HasIndex(b => new { b.CompanyId, b.AirportIcao }).IsUnique(); // one base per airport per company
        companyBase.HasOne<Company>().WithMany().HasForeignKey(b => b.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Traded commodity holdings (Phase 2g) ---
        var lot = model.Entity<InventoryLot>();
        lot.HasKey(l => l.Id);
        lot.Property(l => l.Good).IsRequired().HasMaxLength(40);
        lot.Property(l => l.LocationIcao).IsRequired().HasMaxLength(12);
        lot.HasIndex(l => new { l.CompanyId, l.Good, l.LocationIcao });
        lot.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Market pressure (Phase 7g): your own trading moves the local price ---
        var pressure = model.Entity<MarketPressure>();
        pressure.HasKey(p => p.Id);
        pressure.Property(p => p.Icao).IsRequired().HasMaxLength(12);
        pressure.Property(p => p.Good).IsRequired().HasMaxLength(40);
        pressure.HasIndex(p => new { p.CompanyId, p.Icao, p.Good }).IsUnique(); // one accumulator per market cell
        pressure.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Clients (Phase 8d): named demand-side relationships with per-company loyalty ---
        var client = model.Entity<Client>();
        client.HasKey(c => c.Id);
        client.Property(c => c.ClientKey).IsRequired().HasMaxLength(40);
        client.Property(c => c.Name).IsRequired().HasMaxLength(80);
        client.Property(c => c.HomeIcao).IsRequired().HasMaxLength(12);
        client.HasIndex(c => new { c.CompanyId, c.ClientKey }).IsUnique(); // one loyalty row per client per company
        client.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Operating certificates (Phase 8e): the regulated licences gating premium work ---
        var cert = model.Entity<OperatingCertificate>();
        cert.HasKey(c => c.Id);
        cert.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
        cert.HasIndex(c => new { c.CompanyId, c.Kind }).IsUnique(); // one certificate row per kind per company (renewed in place)
        cert.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Rental agreements (Phase 9f): rentals + leases of non-owned airframes (the tail is Ownership=Rented) ---
        var rental = model.Entity<RentalAgreement>();
        rental.HasKey(r => r.Id);
        rental.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        rental.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        rental.HasIndex(r => r.CompanyId);
        rental.HasIndex(r => r.AircraftInstanceId);
        rental.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
        rental.HasOne<AircraftInstance>().WithMany().HasForeignKey(r => r.AircraftInstanceId).OnDelete(DeleteBehavior.Restrict);

        // --- Pilot licence classes (Phase 3c) ---
        var qual = model.Entity<PilotQualification>();
        qual.HasKey(q => q.Id);
        qual.Property(q => q.Class).HasConversion<string>().HasMaxLength(4);
        qual.HasIndex(q => new { q.PilotId, q.Class }).IsUnique(); // one row per class held
        qual.HasOne<Pilot>().WithMany().HasForeignKey(q => q.PilotId).OnDelete(DeleteBehavior.Cascade);

        // --- Reputation log (Phase 3f), append-only ---
        var rep = model.Entity<ReputationEvent>();
        rep.HasKey(e => e.Id);
        rep.Property(e => e.Id).ValueGeneratedOnAdd();
        rep.Property(e => e.Reason).IsRequired().HasMaxLength(120);
        rep.HasIndex(e => new { e.PilotId, e.At });
        rep.HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId).OnDelete(DeleteBehavior.Cascade);

        // --- Airline operating-reputation log (Phase 11a), append-only, company-scoped ---
        var airRep = model.Entity<AirlineReputationEvent>();
        airRep.HasKey(e => e.Id);
        airRep.Property(e => e.Id).ValueGeneratedOnAdd();
        airRep.Property(e => e.Source).HasDefaultValue(AirlineRepSource.Player); // legacy/back-filled rows read as Player
        airRep.Property(e => e.Reason).IsRequired().HasMaxLength(120);
        airRep.HasIndex(e => new { e.CompanyId, e.At });
        airRep.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);

        // --- Airline enterprise-value history (Phase 13 — "The Flotation"), append-only, company-scoped ---
        var airVal = model.Entity<AirlineValueSnapshot>();
        airVal.HasKey(e => e.Id);
        airVal.Property(e => e.Id).ValueGeneratedOnAdd();
        airVal.HasIndex(e => new { e.CompanyId, e.AtUtc });
        airVal.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);

        // --- Loans (Phase 4a): a liability, distinct from the cash ledger ---
        var loan = model.Entity<Loan>();
        loan.HasKey(l => l.Id);
        loan.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
        loan.HasIndex(l => l.CompanyId);
        loan.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Insurance policies (Phase 4c) ---
        var policy = model.Entity<InsurancePolicy>();
        policy.HasKey(p => p.Id);
        policy.Property(p => p.Scope).HasConversion<string>().HasMaxLength(16);
        policy.Ignore(p => p.CoveredValueCents);   // computed
        policy.Ignore(p => p.ClaimPayoutCents);    // computed
        policy.HasIndex(p => p.CompanyId);
        policy.HasIndex(p => p.AircraftInstanceId);
        policy.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Routes (Phase 4d): scheduled base-to-base lines ---
        var route = model.Entity<Route>();
        route.HasKey(r => r.Id);
        route.Property(r => r.Name).IsRequired().HasMaxLength(80);
        route.Property(r => r.OriginIcao).IsRequired().HasMaxLength(12);
        route.Property(r => r.DestIcao).IsRequired().HasMaxLength(12);
        route.Property(r => r.Mission).HasConversion<string>().HasMaxLength(20);
        route.Property(r => r.PriceMultiplierMilli).HasDefaultValue(1000); // existing routes backfill to the fair rate
        route.Property(r => r.ReliabilityMilli).HasDefaultValue(1000);      // Phase 14c — existing routes backfill as fully reliable (L10)
        route.HasIndex(r => r.CompanyId);
        route.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
        route.HasOne<AircraftInstance>().WithMany().HasForeignKey(r => r.AircraftInstanceId).OnDelete(DeleteBehavior.Restrict);
        route.HasOne<Staff>().WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Restrict);

        // --- Achievements (Phase 5a): earned milestones, one row per (company, catalog key) ---
        var award = model.Entity<AchievementAward>();
        award.HasKey(a => a.Id);
        award.Property(a => a.Key).IsRequired().HasMaxLength(40);
        award.HasIndex(a => new { a.CompanyId, a.Key }).IsUnique(); // earned once — idempotent awarding
        award.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Campaigns (Phase 5b): one progress row per (company, catalog key) ---
        var campaign = model.Entity<CampaignProgress>();
        campaign.HasKey(c => c.Id);
        campaign.Property(c => c.CampaignKey).IsRequired().HasMaxLength(40);
        campaign.HasIndex(c => new { c.CompanyId, c.CampaignKey }).IsUnique();
        campaign.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // --- Challenges (Phase 12): one state row per (company, period, catalog key) ---
        var challenge = model.Entity<ChallengeProgress>();
        challenge.HasKey(c => c.Id);
        challenge.Property(c => c.PeriodKey).IsRequired().HasMaxLength(20);
        challenge.Property(c => c.ChallengeKey).IsRequired().HasMaxLength(40);
        challenge.HasIndex(c => new { c.CompanyId, c.PeriodKey, c.ChallengeKey }).IsUnique();
        challenge.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}
