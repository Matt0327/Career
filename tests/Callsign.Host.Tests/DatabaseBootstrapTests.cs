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
/// migrations-based startup throw <c>SQLite Error 1: 'table "AircraftTypes" already exists'</c> — and the
/// hardening that a legacy save is now preserved as a .bak snapshot rather than deleted.
/// Each test gets its own temp file DB; pooling is off so the file is unlocked for moves/cleanup.
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
    public void PrepareDatabase_RecoversLegacyEnsureCreatedDb_AndKeepsTheOldSave()
    {
        SeedLegacyEnsureCreatedDb();
        using (var db = new CallsignDbContext(Options()))
            CallsignWebApp.PrepareDatabase(db); // must NOT throw — the whole point of the fix

        using (var db = new CallsignDbContext(Options()))
        {
            // The live DB is now a real migrated schema: it has the history table the legacy save lacked.
            Assert.Contains("__EFMigrationsHistory", TableNames(db));
            Assert.Empty(db.Database.GetPendingMigrations());
        }

        // The old save wasn't destroyed — it was set aside as a .bak snapshot that still holds its tables,
        // so a tester who'd made real progress could recover it.
        var snapshot = Assert.Single(BackupSnapshots());
        Assert.Contains("AircraftTypes", TableNamesOf(snapshot));
    }

    [Fact]
    public void PrepareDatabase_OnFreshPath_MigratesCleanly()
    {
        using var db = new CallsignDbContext(Options()); // nothing on disk yet

        CallsignWebApp.PrepareDatabase(db);

        Assert.Empty(db.Database.GetPendingMigrations());
        Assert.Contains("AircraftTypes", TableNames(db));
        Assert.Empty(BackupSnapshots()); // a fresh install leaves no .bak behind
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
        Assert.Empty(BackupSnapshots()); // a healthy save is never backed-up-and-rebuilt
    }

    [Fact]
    public void PrepareDatabase_RecoversAnInconsistentHistory_WhereMigrateWouldStillThrow()
    {
        // The nastier real-world case a tester hit: the app tables are present AND a __EFMigrationsHistory
        // table exists but never recorded InitialCreate. The proactive legacy check sees the history table
        // and passes — so only the Migrate() safety-net can catch it. Reproduce it, then prove recovery.
        SeedLegacyEnsureCreatedDb();
        using (var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" " +
                "(\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (var db = new CallsignDbContext(Options()))
        {
            var ex = Record.Exception(() => CallsignWebApp.PrepareDatabase(db));
            Assert.Null(ex); // must recover, not crash
        }
        using (var db = new CallsignDbContext(Options()))
            Assert.Empty(db.Database.GetPendingMigrations()); // a clean, fully-migrated schema
        Assert.NotEmpty(BackupSnapshots());                    // the old bytes were preserved, not lost
    }

    // The .bak-<timestamp> snapshots RetireLegacyDatabase leaves next to the live DB (excluding sidecars).
    private IEnumerable<string> BackupSnapshots()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        return Directory.EnumerateFiles(dir, Path.GetFileName(_dbPath) + ".bak-*")
            .Where(p => !p.EndsWith("-wal") && !p.EndsWith("-shm"));
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

    private static List<string> TableNamesOf(string dbFilePath)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath};Pooling=False;Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_dbPath)!;
        foreach (var p in Directory.EnumerateFiles(dir, Path.GetFileName(_dbPath) + "*"))
            try { File.Delete(p); } catch { /* best-effort temp cleanup */ }
    }
}
