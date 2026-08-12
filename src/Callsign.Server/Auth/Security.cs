using System.Security.Cryptography;
using System.Text;

namespace Callsign.Server.Auth;

/// <summary>
/// Password hashing with PBKDF2 (HMAC-SHA256, 210k iterations, per-user 16-byte salt). The stored form
/// is self-describing — <c>pbkdf2$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c> — so the cost
/// factor can rise later without breaking existing hashes. No third-party crypto dependency.
/// </summary>
public static class Passwords
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out int iterations)) return false;
        byte[] salt = Convert.FromBase64String(parts[2]);
        byte[] expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected); // constant-time to resist timing attacks
    }
}

/// <summary>
/// Opaque bearer tokens. We mint 32 random bytes (base64url), hand the raw value to the client once, and
/// persist only its SHA-256 — so the token behaves like a password we can't recover but can verify.
/// </summary>
public static class Tokens
{
    public static (string Raw, string Hash) New()
    {
        string raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, HashOf(raw));
    }

    public static string HashOf(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
