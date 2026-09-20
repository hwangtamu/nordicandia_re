using System.Security.Cryptography;

namespace Nordicandia.Server.State;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing. Implemented with BCL primitives so no extra
/// NuGet dependency is required. Encoded format:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c>.
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DefaultIterations = 210_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1000) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}