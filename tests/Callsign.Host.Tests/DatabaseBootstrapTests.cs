using Callsign.Core.Data;
using Callsign.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Host.Tests;

/// <summary>
/// Startup DB robustness (<see cref="CallsignWebApp.PrepareDatabase"/>): a leftover or shipped database
/// must never crash the app on launch. Covers the real regression a tester hit — a pre-migrations save
/// (created by EnsureCreated: the tables exist but there's no __EFMigrationsHistory) that made the new
/// migrations-based startup throw <c>SQLite Error 1: 'table "AircraftTypes" already exists'</c>.
/// Each test gets its own temp file DB; pooling is off so the file is unlocked for EnsureDeleted/cleanup.
/// </summary>
public sealed class DatabaseBootstrapTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"callsign-boot-{Guid.NewGuid():N}.db");

    private DbContextOptions<CallsignDbContext> Options() =>
        new DbContextOptionsBuilder<CallsignDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False").Options;

    // Reproduces a legacy save exactly: EnsureCreated lays down the tables but writes no migration history.
    private void SeedLegacyEnsureCreatedDb()
    {
        using var db = new CallsignDbContext(Options());
        db.Database.EnsureCreated();
    }

    [Fact]
    public void LegacyEnsureCreatedDb_PlainMigrate_Throws_ProvingTheRegression()
    {
        SeedLegacyEnsureCreatedDb();
        using var db = new CallsignDbContext(Options());

        // Without the guard, this is the exact crash the tester saw at startup.
        var ex = Assert.ThrowsAny<Exception>(() => db.Database.Migrate());
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void PrepareDatabase_RecoversLegacyEnsureCreatedDb_WithoutCrashing()
    {
        SeedLegacyEnsureCreatedDb();
        using var db = new CallsignDbContext(Options());

        CallsignWebApp.PrepareDatabase(db); // must NOT throw — the whole point of the fix

        // The DB is now a real migrated schema: it has the history table the legacy save lacked,
        // and nothing is left pending.
        Assert.Contains("__EFMigrationsHistory", TableNames(db));
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public void PrepareDatabase_OnFreshPath_MigratesCleanly()
    {
        using var db = new CallsignDbContext(Options()); // nothing on disk yet

        CallsignWebApp.PrepareDatabase(db);

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Contains("AircraftTypes", TableNames(db));
    }

    [Fact]
    public void PrepareDatabase_LeavesAnAlreadyMigratedSaveIntact()
    {
        var companyId = Guid.NewGuid();
        using (var db = new CallsignDbContext(Options()))
        {
            CallsignWebApp.PrepareDatabase(db); // migrate a clean DB
            db.Companies.Add(new Company { Id = companyId, Name = "Keep me" });
            db.SaveChanges();
        }

        using (var db = new CallsignDbContext(Options()))
        {
            // Second launch: the DB is properly migrated, NOT legacy — the guard must leave the save
            // alone. If PrepareDatabase ever nuked a real DB, this row would be gone.
            CallsignWebApp.PrepareDatabase(db);
            Assert.NotNull(db.Companies.Find(companyId));
        }
    }

    private static List<string> TableNames(CallsignDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
        finally
        {
            if (wasClosed)
                conn.Close();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(p))
                try { File.Delete(p); } catch { /* best-effort temp cleanup */ }
    }
}
