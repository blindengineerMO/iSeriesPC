using System.Security.Cryptography;
using System.Text;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Private per-catalog AES-GCM key ring; keys are outside database backups.</summary>
internal sealed class SecretProtector : IDisposable
{
    private readonly string? _directory;
    private readonly byte[]? _memoryKey;
    private readonly string _memoryId = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private bool _disposed;

    internal SecretProtector(SqliteConnectionFactory factory)
    {
        if (factory.DatabasePath is { } database) _directory = database + ".keys";
        else _memoryKey = RandomNumberGenerator.GetBytes(32);
    }

    internal string Protect(string purpose, ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (plaintext.Length > 65536) throw new ArgumentOutOfRangeException(nameof(plaintext));
            var id = ActiveKey();
            var key = ReadKey(id);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[16];
                using var cipher = new AesGcm(key, 16);
                cipher.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
                return string.Join('.', "v1", id, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    internal byte[] Unprotect(string purpose, string envelope)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (envelope.Length > 100000) throw new CryptographicException("Invalid protected secret.");
            var parts = envelope.Split('.');
            if (parts.Length != 5 || parts[0] != "v1" || !Guid.TryParseExact(parts[1], "N", out _))
                throw new CryptographicException("Invalid protected secret.");
            var key = ReadKey(parts[1]);
            try
            {
                var nonce = Convert.FromBase64String(parts[2]);
                var ciphertext = Convert.FromBase64String(parts[3]);
                var tag = Convert.FromBase64String(parts[4]);
                if (nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("Invalid protected secret.");
                var plaintext = new byte[ciphertext.Length];
                using var cipher = new AesGcm(key, 16);
                try { cipher.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose)); }
                catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
                return plaintext;
            }
            catch (FormatException) { throw new CryptographicException("Invalid protected secret."); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    private string ActiveKey()
    {
        if (_directory is null) return _memoryId;
        if (!Directory.Exists(_directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
            else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        ValidatePrivate(_directory, directory: true);
        var pointer = Path.Combine(_directory, "active");
        if (!File.Exists(pointer))
        {
            var id = Guid.NewGuid().ToString("N");
            var key = RandomNumberGenerator.GetBytes(32);
            try { WritePrivate(Path.Combine(_directory, id + ".key"), key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            // Publish the pointer only after the complete key is durable.
            try { WritePrivate(pointer, Encoding.ASCII.GetBytes(id)); }
            catch (IOException) when (File.Exists(pointer)) { }
        }
        ValidatePrivate(pointer);
        if (new FileInfo(pointer).Length != 32) throw new CryptographicException("Invalid key-ring metadata.");
        var selected = File.ReadAllText(pointer);
        if (!Guid.TryParseExact(selected, "N", out _)) throw new CryptographicException("Invalid key-ring metadata.");
        return selected;
    }

    internal IReadOnlyList<(string Id, bool Active)> Inventory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directory is null) return new[] { (_memoryId, true) };
            if (!Directory.Exists(_directory)) return Array.Empty<(string, bool)>();
            ValidatePrivate(_directory, directory: true);
            var active = ActiveKey();
            return Directory.EnumerateFiles(_directory, "*.key").Select(path =>
            {
                ValidatePrivate(path);
                var id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(id, "N", out _)) throw new CryptographicException("Invalid key-ring filename.");
                return (id, id == active);
            }).OrderBy(item => item.id, StringComparer.Ordinal).ToArray();
        }
    }

    internal string Rotate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directory is null) throw new InvalidOperationException("Wrapping-key rotation requires a persistent catalog.");
            _ = ActiveKey();
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var rotation = new FileStream(Path.Combine(_directory, "rotation.lock"), options);
            var id = Guid.NewGuid().ToString("N");
            var key = RandomNumberGenerator.GetBytes(32);
            try { WritePrivate(Path.Combine(_directory, id + ".key"), key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            WritePrivate(Path.Combine(_directory, "active"), Encoding.ASCII.GetBytes(id), replace: true);
            return id;
        }
    }

    private byte[] ReadKey(string id)
    {
        if (_memoryKey is not null)
            return id == _memoryId ? _memoryKey.ToArray() : throw new CryptographicException("Protected secret belongs to another key ring.");
        ValidatePrivate(_directory!, directory: true);
        var path = Path.Combine(_directory!, id + ".key");
        ValidatePrivate(path);
        if (new FileInfo(path).Length != 32) throw new CryptographicException("Protected-secret key is invalid.");
        return File.ReadAllBytes(path);
    }

    private static void WritePrivate(string path, byte[] bytes, bool replace = false)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidatePrivate(string path, bool directory = false)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null) throw new CryptographicException("Protected-secret key material is missing or linked.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new CryptographicException("Protected-secret key material must be private to the instance owner.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_memoryKey is not null) CryptographicOperations.ZeroMemory(_memoryKey);
        }
    }
}
