using Callsign.Host;
using Callsign.Host.Cloud;
using Xunit;

namespace Callsign.Host.Tests;

// The full cloud round-trip needs a live Host (which this sandbox can't boot — see HostApiTests), but the
// two pieces of new logic that must not fail silently are testable directly: the persisted sign-in session
// and the guard that stops a bad download from replacing a good save.

public class CloudSessionTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "callsign-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Session_persists_across_instances()
    {
        var path = TempPath();
        try
        {
            var first = new CloudSession(path);
            Assert.False(first.IsSignedIn);
            first.Set("tok-123", new CloudProfile("id-1", "a@b.com", "Ace", null));

            var reloaded = new CloudSession(path); // a fresh instance loads from disk, as after a restart
            Assert.True(reloaded.IsSignedIn);
            Assert.Equal("tok-123", reloaded.Token);
            Assert.Equal("a@b.com", reloaded.Profile!.Email);
            Assert.Equal("Ace", reloaded.Profile!.DisplayName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Clear_signs_out_and_deletes_the_file()
    {
        var path = TempPath();
        var session = new CloudSession(path);
        session.Set("tok", new CloudProfile("id", "a@b.com", "Ace", null));
        Assert.True(File.Exists(path));

        session.Clear();
        Assert.False(session.IsSignedIn);
        Assert.False(File.Exists(path));
        Assert.False(new CloudSession(path).IsSignedIn);
    }

    [Fact]
    public void Corrupt_session_file_reads_as_signed_out()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            Assert.False(new CloudSession(path).IsSignedIn);
        }
        finally { File.Delete(path); }
    }
}

public class StageRestoreTests
{
    [Fact]
    public void Rejects_bytes_that_are_not_a_sqlite_database()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var saves = new SaveService(Path.Combine(dir, "callsign.db"));
            Assert.Throws<InvalidDataException>(() => saves.StageRestoreFromBytes(new byte[] { 1, 2, 3, 4 }));
            Assert.False(File.Exists(Path.Combine(dir, "callsign.db.restore-pending")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Stages_a_valid_sqlite_download_for_next_launch()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dbPath = Path.Combine(dir, "callsign.db");
            var saves = new SaveService(dbPath);

            var payload = new byte[100];
            System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(payload, 0);
            saves.StageRestoreFromBytes(payload);

            Assert.True(File.Exists(dbPath + ".restore-pending"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
