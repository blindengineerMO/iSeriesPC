using System.Security.Cryptography;

namespace Ipc.Services.Security;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password, int iterations = 210_000)
    {
        if (iterations is < 10_000 or > 2_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Pbkdf2(password, salt, iterations);
        return $"{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var iterations) || iterations is < 10_000 or > 2_000_000) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[1]); expected = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return false; }
        if (salt.Length != SaltSize || expected.Length != KeySize) return false;
        var actual = Pbkdf2(password, salt, iterations);
        return FixedTimeEquals(expected, actual);
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }
}
