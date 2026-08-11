using Callsign.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Data;

/// <summary>The SQLite-backed database for a single save. One file, easy backup (brief §5.1).</summary>
public sealed class CallsignDbContext : DbContext
{
    public CallsignDbContext(DbContextOptions<CallsignDbContext> options) : base(options) { }

    public DbSet<Pilot> Pilots => Set<Pilot>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var pilot = model.Entity<Pilot>();
        pilot.HasKey(p => p.Id);
        pilot.Property(p => p.Name).IsRequired().HasMaxLength(80);
        pilot.Property(p => p.HomeIcao).IsRequired().HasMaxLength(8);
        pilot.Property(p => p.CurrentIcao).IsRequired().HasMaxLength(8);

        var ledger = model.Entity<LedgerEntry>();
        ledger.HasKey(e => e.Id);
        ledger.Property(e => e.Id).ValueGeneratedOnAdd();
        ledger.Property(e => e.Description).IsRequired().HasMaxLength(200);
        ledger.Property(e => e.RelatedEntityId).HasMaxLength(80);
        ledger.HasIndex(e => new { e.PilotId, e.At });
        ledger.HasOne<Pilot>()
              .WithMany()
              .HasForeignKey(e => e.PilotId)
              .OnDelete(DeleteBehavior.Cascade);
    }
}
