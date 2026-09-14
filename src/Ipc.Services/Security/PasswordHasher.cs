using System.Security.Cryptography;

namespace Ipc.Services.Security;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password, int iterations = 210_000)
    {
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

        var iterations = int.Parse(parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
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