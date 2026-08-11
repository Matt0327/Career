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
        // Global identity + per-account idempotency.
        ledger.HasIndex(e => e.EntryUid).IsUnique();
        ledger.HasIndex(e => new { e.AccountId, e.DedupeKey }).IsUnique();
        // Running balance + per-asset P&L rollups.
        ledger.HasIndex(e => new { e.AccountId, e.At });
        ledger.HasIndex(e => new { e.AircraftInstanceId, e.At });
        ledger.HasIndex(e => new { e.StaffId, e.At });
        ledger.HasIndex(e => new { e.BaseId, e.At });
        ledger.HasIndex(e => new { e.RelatedEntityType, e.RelatedEntityId });
        // Never cascade — deleting an account must never delete the source-of-truth ledger.
        ledger.HasOne<Company>().WithMany().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
