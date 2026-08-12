using Callsign.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Callsign.Server.Data;

/// <summary>The Callsign Cloud database: accounts, active tokens, and cloud saves.</summary>
public sealed class ServerDbContext : DbContext
{
    public ServerDbContext(DbContextOptions<ServerDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuthToken> Tokens => Set<AuthToken>();
    public DbSet<CloudSave> Saves => Set<CloudSave>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite (dev) has no native DateTimeOffset — store it as a sortable long, as Callsign.Core does.
        // On Postgres (deploy) this converter is dropped and the type is native.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        var user = model.Entity<AppUser>();
        user.HasKey(u => u.Id);
        user.Property(u => u.Email).IsRequired().HasMaxLength(160);
        user.HasIndex(u => u.Email).IsUnique();          // email is the identity
        user.Property(u => u.DisplayName).IsRequired().HasMaxLength(40);
        user.Property(u => u.PasswordHash).IsRequired();

        var token = model.Entity<AuthToken>();
        token.HasKey(t => t.Id);
        token.Property(t => t.TokenHash).IsRequired();
        token.HasIndex(t => t.TokenHash).IsUnique();     // the lookup key on every authed request
        token.HasIndex(t => t.UserId);

        var save = model.Entity<CloudSave>();
        save.HasKey(s => s.Id);
        save.HasIndex(s => s.UserId).IsUnique();         // latest-wins: one cloud save per user
    }
}
