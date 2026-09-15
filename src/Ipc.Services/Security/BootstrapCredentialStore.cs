using System.Security.Cryptography;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Host-only enrollment material. The catalog stores only its password hash.</summary>
internal sealed class BootstrapCredentialStore(SqliteConnectionFactory factory)
{
    internal string? PathName => factory.DatabasePath is { } database ? database + ".initial-password" : null;

    internal string Prepare(PasswordPolicy policy)
    {
        var length = Math.Max(policy.MinimumLength, Math.Min(24, policy.MaximumLength));
        const string ascii = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var alphabet = (length <= ascii.Length ? ascii : ascii +
            new string(Enumerable.Range(0x100, 160).Select(c => (char)c).ToArray())).ToList();
        // Sampling without replacement also satisfies the no-repeat policy.
        var characters = new List<char> { (char)('2' + RandomNumberGenerator.GetInt32(8)) };
        alphabet.Remove(characters[0]);
        for (var i = 1; i < length; i++)
        {
            var index = RandomNumberGenerator.GetInt32(alphabet.Count);
            characters.Add(alphabet[index]);
            alphabet.RemoveAt(index);
        }
        var password = new string(characters.ToArray());
        if (!policy.IsValid(password)) throw new InvalidOperationException("Cannot generate an initial credential for the configured password policy.");
        if (PathName is not { } path) return password;
        if (File.Exists(path))
        {
            if (new FileInfo(path).LinkTarget is not null || !OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("Initial credential file must be private and must not be a symbolic link.");
            var existing = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (!policy.IsValid(existing))
                throw new IOException("Initial credential file is invalid; recover it before starting the catalog.");
            return existing;
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.WriteLine(password);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return password;
    }

    internal void Remove()
    {
        if (PathName is { } path) File.Delete(path);
    }
}
