using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ipc.Services.Security;

/// <summary>RFC 6238 SHA-1, 30-second steps; application verification uses six digits.</summary>
public static class TotpCode
{
    public static string Generate(ReadOnlySpan<byte> secret, long unixSeconds, int digits = 6)
    {
        if (secret.Length < 20) throw new ArgumentException("TOTP requires at least 160 bits of secret material.", nameof(secret));
        if (unixSeconds < 0) throw new ArgumentOutOfRangeException(nameof(unixSeconds));
        if (digits is not (6 or 8)) throw new ArgumentOutOfRangeException(nameof(digits));
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, unixSeconds / 30);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 15;
        var truncated = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7fffffff;
        return (truncated % (digits == 6 ? 1_000_000 : 100_000_000)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    public static long? Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, long lastUsedStep = -1)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) return null;
        var current = now.ToUnixTimeSeconds() / 30;
        var supplied = Encoding.ASCII.GetBytes(code);
        long? matched = null;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = current + offset;
            if (step < 0) continue;
            var expected = Encoding.ASCII.GetBytes(Generate(secret, step * 30));
            if (CryptographicOperations.FixedTimeEquals(supplied, expected) && step > lastUsedStep) matched = step;
        }
        return matched;
    }

    public static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var bits = 0;
        var value = 0;
        foreach (var item in bytes)
        {
            value = (value << 8) | item;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(alphabet[(value >> bits) & 31]); }
        }
        if (bits > 0) result.Append(alphabet[(value << (5 - bits)) & 31]);
        return result.ToString();
    }
}
