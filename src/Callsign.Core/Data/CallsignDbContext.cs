using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;

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
    }
}
