using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Callsign.Core.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations …</c> can construct the context outside the app's DI.
/// The connection string is irrelevant here — no database is opened while scaffolding a migration.
/// </summary>
public sealed class CallsignDbContextFactory : IDesignTimeDbContextFactory<CallsignDbContext>
{
    public CallsignDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<CallsignDbContext>().UseSqlite("Data Source=callsign-design.db").Options);
}
