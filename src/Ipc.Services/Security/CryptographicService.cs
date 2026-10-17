using System.Security.Cryptography;
using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Software cryptography and opaque job-owned keys; no plaintext-key export operation.</summary>
public sealed class CryptographicService(SqliteConnectionFactory factory)
{
    public const int MaximumDataLength = 1048576;
    public const int MaximumKeyLength = 65536;
    internal static HashAlgorithmName HashName(int algorithm) => algorithm switch {
        1 => HashAlgorithmName.MD5, 2 => HashAlgorithmName.SHA1, 3 => HashAlgorithmName.SHA256,
        4 => HashAlgorithmName.SHA384, 5 => HashAlgorithmName.SHA512,
        _ => throw new CpfException("CPF9DF0", "Hash algorithm is not available in this software provider.") };
    public static int HashLength(int algorithm) => algorithm switch { 1 => 16, 2 => 20, 3 => 32, 4 => 48, 5 => 64, _ => throw new CpfException("CPF9DF0", "Unsupported hash algorithm.") };
    internal static void ValidateKey(int type, int length)
    {
        if (type != 22 && type is not (>= 1 and <= 5)) throw new CpfException("CPF9DE7", "Unsupported key type.");
        if (type == 22 ? length is not (16 or 24 or 32) : length < HashLength(type) || length > MaximumKeyLength)
            throw new CpfException("CPF9DDD", "Key length is not valid for the selected algorithm.");
    }
    public byte[] Hash(int algorithm, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        Check(data.Length, cancellationToken); using var hash = IncrementalHash.CreateHash(HashName(algorithm)); hash.AppendData(data);
        var result = hash.GetHashAndReset(); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    public byte[] Hmac(int algorithm, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        Check(data.Length, cancellationToken); ValidateKey(algorithm, key.Length); var copy = key.ToArray();
        try { using var hash = IncrementalHash.CreateHMAC(HashName(algorithm), copy); hash.AppendData(data); var result = hash.GetHashAndReset(); cancellationToken.ThrowIfCancellationRequested(); return result; }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }
    public byte[] Hmac(JobKey job, byte[] token, int algorithm, byte[] data, CancellationToken cancellationToken = default)
        => factory.CryptoKeys.Use(job, token, algorithm, key => Hmac(algorithm, key, data, cancellationToken));
    public byte[] CreateKeyContext(JobKey job, int type, ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
        => factory.CryptoKeys.Create(job, type, key, null, cancellationToken);
    internal byte[] CreateKeyContext(JobKey job, int type, ReadOnlySpan<byte> key, Action<byte[]> prepare, CancellationToken cancellationToken)
        => factory.CryptoKeys.Create(job, type, key, prepare, cancellationToken);
    public void DestroyKeyContext(JobKey job, byte[] token, CancellationToken cancellationToken = default) => factory.CryptoKeys.Destroy(job, token, cancellationToken);
    public byte[] Transform(JobKey job, byte[] token, byte[] data, int mode, int padding, byte padCharacter, byte[] iv, bool decrypt, CancellationToken cancellationToken = default)
        => factory.CryptoKeys.Use(job, token, 22, key => Transform(key, data, mode, padding, padCharacter, iv, decrypt, cancellationToken));
    public byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, int mode, int padding, byte padCharacter, ReadOnlySpan<byte> iv, bool decrypt, CancellationToken cancellationToken = default)
    {
        Check(data.Length, cancellationToken); ValidateKey(22, key.Length);
        if (mode is not (0 or 1 or 7)) throw new CpfException("CPF9DF0", "AES mode is not available in this provider.");
        if (padding < 0 || padding > (decrypt ? 1 : 2)) throw new CpfException("CPF9DE4", "Invalid pad option.");
        if (iv.Length != 16 || mode == 0 && iv.IndexOfAnyExcept((byte)0) >= 0) throw new CpfException("CPF9DEE", "Invalid AES IV or nonzero unused IV.");
        if (decrypt && padCharacter != 0) throw new CpfException("CPF9DEE", "Decrypt pad character must be binary zero.");
        var input = data.ToArray(); var keyCopy = key.ToArray();
        try
        {
            if (!decrypt && padding != 0)
            {
                var count = 16 - input.Length % 16; var padded = new byte[input.Length + count]; input.CopyTo(padded, 0);
                padded.AsSpan(input.Length, count - 1).Fill(padding == 2 ? (byte)count : padCharacter); padded[^1] = (byte)count;
                CryptographicOperations.ZeroMemory(input); input = padded;
            }
            if (mode != 7 && input.Length % 16 != 0) throw new CpfException(decrypt ? "CPF9DC3" : "CPF9DD5", "Input length is not a multiple of the AES block length.");
            using var aes = Aes.Create(); aes.Key = keyCopy; byte[] result;
            if (mode == 7) result = Counter(aes, input, iv, cancellationToken);
            else if (mode == 0) result = decrypt ? aes.DecryptEcb(input, PaddingMode.None) : aes.EncryptEcb(input, PaddingMode.None);
            else result = decrypt ? aes.DecryptCbc(input, iv, PaddingMode.None) : aes.EncryptCbc(input, iv, PaddingMode.None);
            if (decrypt && padding != 0)
            {
                // Native decrypt removes the final binary pad count, regardless of the encrypt pad character.
                if (result.Length == 0 || result[^1] is < 1 or > 16 || result[^1] > result.Length)
                { CryptographicOperations.ZeroMemory(result); throw new CpfException("CPF9DC3", "Unable to decrypt data or remove padding."); }
                var trimmed = result.AsSpan(0, result.Length - result[^1]).ToArray(); CryptographicOperations.ZeroMemory(result); result = trimmed;
            }
            try { cancellationToken.ThrowIfCancellationRequested(); return result; }
            catch { CryptographicOperations.ZeroMemory(result); throw; }
        }
        catch (CryptographicException) { throw new CpfException("CPF9DC3", "Cryptographic operation failed."); }
        finally { CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(keyCopy); }
    }
    private static byte[] Counter(Aes aes, byte[] input, ReadOnlySpan<byte> iv, CancellationToken cancellationToken)
    {
        var counter = iv.ToArray(); var result = new byte[input.Length]; var stream = new byte[16];
        try
        {
            for (var offset = 0; offset < input.Length; offset += 16)
            {
                cancellationToken.ThrowIfCancellationRequested(); if ((offset & 4095) == 0) JobExecutionBudget.Checkpoint(); aes.EncryptEcb(counter, stream, PaddingMode.None);
                for (var n = 0; n < Math.Min(16, input.Length - offset); n++) result[offset + n] = (byte)(input[offset + n] ^ stream[n]);
                for (var n = 15; n >= 0 && ++counter[n] == 0; n--) { }
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(stream); }
    }
    private void Check(int length, CancellationToken cancellationToken)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.None); cancellationToken.ThrowIfCancellationRequested();
        if (length > MaximumDataLength) throw new CpfException("CPF9DD5", "Cryptographic input is limited to 1 MiB.");
    }
}
