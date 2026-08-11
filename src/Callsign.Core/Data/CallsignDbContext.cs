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
        job.HasIndex(j => new { j.OriginIcao, j.ExpiresAt });
    }
}
