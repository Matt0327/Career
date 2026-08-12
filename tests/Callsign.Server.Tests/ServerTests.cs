using Callsign.Server.Auth;
using Callsign.Server.Data;
using Callsign.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Callsign.Server.Tests;

// The web host itself can't be booted in this sandbox (the same net10 host-resolver timeout the Host
// project documents), so we verify the load-bearing pieces directly: password crypto, opaque tokens, and
// the schema's integrity constraints. These are exactly the parts where a silent bug would be dangerous.

public class PasswordTests
{
    [Fact]
    public void Hash_then_verify_roundtrips()
    {
        string hash = Passwords.Hash("supersecret");
        Assert.StartsWith("pbkdf2$", hash);
        Assert.True(Passwords.Verify("supersecret", hash));
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        string hash = Passwords.Hash("supersecret");
        Assert.False(Passwords.Verify("not-it", hash));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time() // i.e. it is salted
    {
        Assert.NotEqual(Passwords.Hash("repeat"), Passwords.Hash("repeat"));
    }

    [Fact]
    public void Malformed_stored_hash_returns_false_and_never_throws()
    {
        Assert.False(Passwords.Verify("x", "garbage"));
        Assert.False(Passwords.Verify("x", "pbkdf2$notanumber$c2FsdA==$aGFzaA=="));
        Assert.False(Passwords.Verify("x", ""));
    }
}

public class TokenTests
{
    [Fact]
    public void New_yields_unique_raw_tokens_whose_hash_matches()
    {
        var (raw1, hash1) = Tokens.New();
        var (raw2, _) = Tokens.New();
        Assert.NotEqual(raw1, raw2);
        Assert.Equal(hash1, Tokens.HashOf(raw1));
    }

    [Fact]
    public void Raw_token_is_url_safe()
    {
        var (raw, _) = Tokens.New();
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
    }

    [Fact]
    public void HashOf_is_deterministic_and_hides_the_raw_value()
    {
        var (raw, hash) = Tokens.New();
        Assert.Equal(hash, Tokens.HashOf(raw));
        Assert.NotEqual(raw, hash); // we persist the hash, not the token
    }
}

public class SchemaTests
{
    // Each test gets its own kept-open in-memory SQLite database with the real schema.
    private static ServerDbContext NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(conn).Options;
        var db = new ServerDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void Schema_creates_and_persists_an_account()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Users.Add(new AppUser { Email = "a@b.com", DisplayName = "A", PasswordHash = "h" });
        db.SaveChanges();
        Assert.Equal(1, db.Users.Count());
    }

    [Fact]
    public void Email_is_unique()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Users.Add(new AppUser { Email = "dup@b.com", DisplayName = "One", PasswordHash = "h" });
        db.SaveChanges();
        db.Users.Add(new AppUser { Email = "dup@b.com", DisplayName = "Two", PasswordHash = "h" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void A_user_has_at_most_one_cloud_save()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        var userId = Guid.NewGuid();
        db.Saves.Add(new CloudSave { UserId = userId, Data = new byte[] { 1 }, SizeBytes = 1 });
        db.SaveChanges();
        db.Saves.Add(new CloudSave { UserId = userId, Data = new byte[] { 2 }, SizeBytes = 1 });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void A_token_resolves_back_to_its_user_by_hash()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        var (raw, hash) = Tokens.New();
        var userId = Guid.NewGuid();
        db.Tokens.Add(new AuthToken { UserId = userId, TokenHash = hash });
        db.SaveChanges();

        var found = db.Tokens.FirstOrDefault(t => t.TokenHash == Tokens.HashOf(raw));
        Assert.NotNull(found);
        Assert.Equal(userId, found!.UserId);
    }
}

public class ImageIndexTests
{
    private static ServerDbContext NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(conn).Options;
        var db = new ServerDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    // Mirrors the server's serve query: the preferred APPROVED image for a key (highest rank, then newest).
    private static Task<AircraftImage?> BestApproved(ServerDbContext db, string key) =>
        db.Images.Where(i => i.Key == key && i.Status == ImageStatus.Approved)
                 .OrderByDescending(i => i.SortRank).ThenByDescending(i => i.CreatedAt)
                 .FirstOrDefaultAsync();

    [Fact]
    public async Task Pending_images_are_not_served()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Images.Add(new AircraftImage { Key = "C172", Status = ImageStatus.Pending, License = "CC BY", Attribution = "x", Data = new byte[] { 1 } });
        await db.SaveChangesAsync();
        Assert.Null(await BestApproved(db, "C172"));
    }

    [Fact]
    public async Task Highest_ranked_approved_image_wins_and_rejected_is_ignored()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Images.Add(new AircraftImage { Key = "C172", Status = ImageStatus.Approved, SortRank = 1, License = "CC BY", Attribution = "a", Data = new byte[] { 1 } });
        db.Images.Add(new AircraftImage { Key = "C172", Status = ImageStatus.Approved, SortRank = 5, License = "CC BY", Attribution = "b", Data = new byte[] { 2 } });
        db.Images.Add(new AircraftImage { Key = "C172", Status = ImageStatus.Rejected, SortRank = 9, License = "CC BY", Attribution = "c", Data = new byte[] { 3 } });
        await db.SaveChangesAsync();

        var best = await BestApproved(db, "C172");
        Assert.NotNull(best);
        Assert.Equal(5, best!.SortRank);        // the rank-9 image is rejected, so it never serves
        Assert.Equal("b", best.Attribution);
    }

    [Fact]
    public async Task Images_are_isolated_by_key()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Images.Add(new AircraftImage { Key = "C172", Status = ImageStatus.Approved, License = "CC0", Attribution = "a", Data = new byte[] { 1 } });
        await db.SaveChangesAsync();
        Assert.NotNull(await BestApproved(db, "C172"));
        Assert.Null(await BestApproved(db, "B738"));
    }
}

public class LeaderboardTests
{
    private static ServerDbContext NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(conn).Options;
        var db = new ServerDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static LeaderboardStat Player(string name, long netWorth, int flights) =>
        new() { UserId = Guid.NewGuid(), DisplayName = name, NetWorthCents = netWorth, Flights = flights };

    [Fact]
    public async Task Networth_board_ranks_richest_first()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Leaderboard.AddRange(Player("A", 500, 2), Player("B", 9000, 1), Player("C", 3000, 9));
        await db.SaveChangesAsync();

        var order = await db.Leaderboard.OrderByDescending(x => x.NetWorthCents).Select(x => x.DisplayName).ToListAsync();
        Assert.Equal(new[] { "B", "C", "A" }, order);
    }

    [Fact]
    public async Task Different_boards_rank_differently()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        db.Leaderboard.AddRange(Player("A", 500, 2), Player("B", 9000, 1), Player("C", 3000, 9));
        await db.SaveChangesAsync();

        var topFlights = await db.Leaderboard.OrderByDescending(x => x.Flights).Select(x => x.DisplayName).FirstAsync();
        var topWorth = await db.Leaderboard.OrderByDescending(x => x.NetWorthCents).Select(x => x.DisplayName).FirstAsync();
        Assert.Equal("C", topFlights);   // most flights
        Assert.Equal("B", topWorth);     // richest
    }

    [Fact]
    public async Task Resubmitting_updates_the_same_row_not_a_new_one()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        using var db = NewDb(conn);
        var userId = Guid.NewGuid();

        async Task Submit(long netWorth)
        {
            var row = await db.Leaderboard.FindAsync(userId);
            if (row is null) { row = new LeaderboardStat { UserId = userId, DisplayName = "Ace" }; db.Leaderboard.Add(row); }
            row.NetWorthCents = netWorth;
            await db.SaveChangesAsync();
        }

        await Submit(1000);
        await Submit(5000);

        Assert.Equal(1, await db.Leaderboard.CountAsync());
        Assert.Equal(5000, (await db.Leaderboard.FindAsync(userId))!.NetWorthCents);
    }
}
