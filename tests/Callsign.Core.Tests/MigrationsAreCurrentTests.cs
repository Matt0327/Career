using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>
/// Guards that the EF model and the migrations stay in lockstep. The shipped app upgrades saves in place
/// with Database.Migrate() at startup, so if someone changes an entity but forgets to add a migration,
/// existing databases would silently drift from the model. HasPendingModelChanges() catches that here,
/// at test time, instead of on a player's machine.
/// </summary>
public class MigrationsAreCurrentTests
{
    [Fact]
    public void Model_HasNoChangesThatAMigrationHasNotCaptured()
    {
        using var tdb = new TestDb();
        using var db = tdb.NewContext();
        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model has changes with no matching migration — run `dotnet ef migrations add <Name>`.");
    }
}
