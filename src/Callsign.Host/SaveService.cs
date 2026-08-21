using Callsign.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Host;

/// <summary>
/// Manages the physical save file next to the live database: on-demand backups (a clean
/// <c>VACUUM INTO</c> snapshot taken while the app keeps running), listing and exporting them, and
/// staging a restore. The live file can't be swapped while it's open, so a restore is *staged* and
/// applied by <see cref="ApplyPendingRestore"/> at the next startup, before anything opens the DB.
/// Registered as a singleton built from the resolved <c>Db:Path</c>.
/// </summary>
public sealed class SaveService
{
    public string DbPath { get; }
    public string BackupsDir { get; }

    public SaveService(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        BackupsDir = Path.Combine(Path.GetDirectoryName(DbPath)!, "backups");
    }

    public sealed record BackupInfo(string Name, long SizeBytes, DateTime CreatedUtc);

    private string PendingRestorePath => DbPath + ".restore-pending";

    /// <summary>Snapshot the current save into the backups folder; returns the new file's info.</summary>
    public async Task<BackupInfo> BackupAsync(CallsignDbContext db, DateTime utcNow)
    {
        Directory.CreateDirectory(BackupsDir);
        // VACUUM INTO writes a consistent, fully-checkpointed single-file copy with no manual WAL juggling
        // and no torn state, even while the app is live. Its target is a SQL string literal (parameters
        // aren't allowed there), so the app-controlled path has its quotes escaped defensively.
        var (name, dest) = FreeBackupPath(utcNow);
        // Plain (non-interpolated) SQL string: the path is app-generated and its quotes are escaped, and
        // VACUUM INTO takes no parameters — so this is safe despite being a "raw" execute.
        var sql = "VACUUM INTO '" + dest.Replace("'", "''") + "'";
        await db.Database.ExecuteSqlRawAsync(sql);
        var fi = new FileInfo(dest);
        return new BackupInfo(name, fi.Length, fi.CreationTimeUtc);
    }

    // A backup name for this second, disambiguated if one already exists (VACUUM INTO needs a fresh path).
    private (string Name, string Path) FreeBackupPath(DateTime utcNow)
    {
        var stamp = utcNow.ToString("yyyyMMdd-HHmmss");
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"callsign-{stamp}.db" : $"callsign-{stamp}-{n}.db";
            var path = Path.Combine(BackupsDir, name);
            if (!File.Exists(path))
                return (name, path);
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(BackupsDir))
            return Array.Empty<BackupInfo>();
        return Directory.EnumerateFiles(BackupsDir, "callsign-*.db")
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.Name) // timestamped names sort newest-first
            .Select(fi => new BackupInfo(fi.Name, fi.Length, fi.CreationTimeUtc))
            .ToList();
    }

    /// <summary>Full path of a named backup, or null if the name is unknown or unsafe (no traversal).</summary>
    public string? ResolveBackup(string name)
    {
        // Reject separators, parent refs, and colons (a drive letter like "C:x" or an alternate-data-stream
        // "x:y" both escape without ever containing a slash), then CANONICALIZE and confirm the resolved path
        // is genuinely inside BackupsDir — Path.Combine can otherwise send a drive-relative name elsewhere.
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Contains(':'))
            return null;
        var root = Path.GetFullPath(BackupsDir);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Snapshot the live save to an arbitrary destination (e.g. a temp file to upload to the cloud). Same
    /// consistent <c>VACUUM INTO</c> as a backup, but the caller owns the destination and its lifetime.
    /// </summary>
    public async Task SnapshotToAsync(CallsignDbContext db, string destPath)
    {
        var full = Path.GetFullPath(destPath);
        var sql = "VACUUM INTO '" + full.Replace("'", "''") + "'";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>Stage a backup to become the live save on next launch (the open file can't be swapped now).</summary>
    public void StageRestore(string backupPath) => File.Copy(backupPath, PendingRestorePath, overwrite: true);

    /// <summary>
    /// Stage downloaded cloud-save bytes to become the live save next launch. The bytes are validated as a
    /// real SQLite database first, so a truncated or corrupt download can never overwrite a good save.
    /// </summary>
    public void StageRestoreFromBytes(byte[] data)
    {
        if (!LooksLikeSqlite(data))
            throw new InvalidDataException("The downloaded save is not a valid Callsign database.");
        File.WriteAllBytes(PendingRestorePath, data);
    }

    // Every SQLite file starts with the 16-byte header "SQLite format 3\0".
    private static readonly byte[] SqliteMagic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static bool LooksLikeSqlite(byte[] data) =>
        data.Length >= SqliteMagic.Length && data.AsSpan(0, SqliteMagic.Length).SequenceEqual(SqliteMagic);

    /// <summary>
    /// If a restore was staged, apply it before the DB is opened: the current save is moved aside to a
    /// <c>.bak-&lt;timestamp&gt;</c> (never destroyed) and the staged file becomes the live save. Best-effort —
    /// any failure leaves the current save in place rather than blocking startup.
    /// </summary>
    public static void ApplyPendingRestore(string dbPath)
    {
        try
        {
            dbPath = Path.GetFullPath(dbPath);
            var pending = dbPath + ".restore-pending";
            if (!File.Exists(pending))
                return;
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                var backup = $"{dbPath}.bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var src = dbPath + suffix;
                    if (File.Exists(src))
                        File.Move(src, backup + suffix);
                }
            }
            File.Move(pending, dbPath);
        }
        catch
        {
            // Leave the current save untouched; a restore is never worth crashing the app over.
        }
    }
}
