using Callsign.Core.Data;
using Callsign.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Host.Tests;

/// <summary>
/// The save-management service: on-demand VACUUM INTO snapshots, and a staged restore that the next
/// startup applies by swapping files (moving the replaced save aside, never destroying it).
/// Each test works inside its own temp directory; pooling is off so files unlock for the swaps.
/// </summary>
public sealed class SaveServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"callsign-save-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_dir, "callsign.db");

    public SaveServiceTests() => Directory.CreateDirectory(_dir);

    private static DbContextOptions<CallsignDbContext> OptionsFor(string path) =>
        new DbContextOptionsBuilder<CallsignDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;

    // A fully-migrated DB at `path` holding one company; returns its id.
    private static Guid SeedCompany(string path, string name)
    {
        var id = Guid.NewGuid();
        using var db = new CallsignDbContext(OptionsFor(path));
        CallsignWebApp.PrepareDatabase(db);
        db.Companies.Add(new Company { Id = id, Name = name });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Backup_WritesASnapshotThatHoldsTheSameData()
    {
        var id = SeedCompany(DbPath, "Snapshot Air");
        var saves = new SaveService(DbPath);

        SaveService.BackupInfo info;
        using (var db = new CallsignDbContext(OptionsFor(DbPath)))
            info = await saves.BackupAsync(db, new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Utc));

        Assert.True(info.SizeBytes > 0);
        var backupPath = Path.Combine(saves.BackupsDir, info.Name);
        Assert.True(File.Exists(backupPath));
        Assert.Contains(saves.List(), b => b.Name == info.Name);

        // The snapshot is a real, openable DB that still has the company we saved.
        using var snap = new CallsignDbContext(OptionsFor(backupPath));
        Assert.NotNull(snap.Companies.Find(id));
    }

    [Fact]
    public async Task Restore_StagedThenApplied_SwapsInTheBackup_AndKeepsTheReplacedSave()
    {
        // A live save holding "Original", plus a snapshot taken from a DB holding "Restored".
        var originalId = SeedCompany(DbPath, "Original Air");
        var sourceDb = Path.Combine(_dir, "source.db");
        var restoredId = SeedCompany(sourceDb, "Restored Air");

        var saves = new SaveService(DbPath);
        string backupName;
        using (var db = new CallsignDbContext(OptionsFor(sourceDb)))
            backupName = (await saves.BackupAsync(db, new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc))).Name;

        saves.StageRestore(Path.Combine(saves.BackupsDir, backupName));
        SaveService.ApplyPendingRestore(DbPath); // the swap the next launch performs

        using var live = new CallsignDbContext(OptionsFor(DbPath));
        Assert.NotNull(live.Companies.Find(restoredId));            // the restore took effect
        Assert.Null(live.Companies.Find(originalId));               // it replaced the old content
        Assert.False(File.Exists(DbPath + ".restore-pending"));     // the staging file was consumed
        Assert.NotEmpty(Directory.EnumerateFiles(_dir, "callsign.db.bak-*")); // old save preserved, not lost
    }

    [Fact]
    public void ApplyPendingRestore_NoPendingFile_IsANoOp()
    {
        var id = SeedCompany(DbPath, "Untouched Air");

        SaveService.ApplyPendingRestore(DbPath); // nothing staged

        using var db = new CallsignDbContext(OptionsFor(DbPath));
        Assert.NotNull(db.Companies.Find(id));
        Assert.Empty(Directory.EnumerateFiles(_dir, "callsign.db.bak-*"));
    }

    [Fact]
    public void ResolveBackup_RejectsUnknownAndTraversalNames()
    {
        var saves = new SaveService(DbPath);
        Assert.Null(saves.ResolveBackup("does-not-exist.db"));
        Assert.Null(saves.ResolveBackup("../callsign.db"));
        Assert.Null(saves.ResolveBackup("..\\callsign.db"));
        Assert.Null(saves.ResolveBackup(""));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
