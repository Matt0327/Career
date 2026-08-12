namespace Callsign.Server.Domain;

/// <summary>A Callsign account. Email is the identity; the password is stored only as a PBKDF2 hash.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }                 // moderates the aircraft-image index
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A bearer token issued at sign-in. We persist only the SHA-256 of the token (never the raw value),
/// so a database leak can't be replayed as a live session. One row per active device sign-in.
/// </summary>
public sealed class AuthToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(90);
}

/// <summary>
/// The player's cloud save: their local SQLite save uploaded as an opaque blob, latest-wins, one per
/// user. This is the honest MVP of "your career, anywhere" — a full per-entity sync engine (the dormant
/// <c>ISyncable</c> hooks) is the later shared-world step, not this.
/// </summary>
public sealed class CloudSave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public long SizeBytes { get; set; }
    public string? Device { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
