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
