using Callsign.Core.Data;
using Callsign.Core.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Core.Tests;

/// <summary>
/// A private in-memory SQLite database for tests. The connection is held open for the
/// lifetime of this handle so the schema and data persist across contexts.
/// </summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public DbContextOptions<CallsignDbContext> Options { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        Options = new DbContextOptionsBuilder<CallsignDbContext>().UseSqlite(_conn).Options;
        using var ctx = new CallsignDbContext(Options);
        ctx.Database.EnsureCreated();
    }

    public CallsignDbContext NewContext() => new(Options);

    public void Dispose() => _conn.Dispose();
}

/// <summary>Controllable clock for deterministic tests.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
