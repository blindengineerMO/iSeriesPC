using System.DirectoryServices.Protocols;
using System.Net;
using Ipc.Services.Configuration;

namespace Ipc.Services.Security;

public interface IDirectoryIdentityProbe
{
    DirectoryIdentityState Check(EimMapping mapping);
}

public sealed class LdapIdentityProbe : IDirectoryIdentityProbe
{
    private readonly Dictionary<string, LdapDirectoryConfig> _directories;
    public LdapIdentityProbe(IEnumerable<LdapDirectoryConfig> directories)
    {
        _directories = new(StringComparer.Ordinal);
        foreach (var source in directories)
        {
            var directory = System.Text.Json.JsonSerializer.Deserialize<LdapDirectoryConfig>(System.Text.Json.JsonSerializer.Serialize(source))!;
            if (string.IsNullOrWhiteSpace(directory.Name) || !_directories.TryAdd(directory.Name, directory))
                throw new ArgumentException("Directory names must be nonempty and unique.");
            var uri = new Uri(directory.Uri, UriKind.Absolute);
            if (uri.Scheme != "ldaps" && !(uri.Scheme == "ldap" && directory.AllowLoopbackPlaintext &&
                IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)))
                throw new ArgumentException("LDAP requires LDAPS; plaintext is allowed only at an explicitly enabled loopback IP.");
            if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/"))
                throw new ArgumentException("Directory URI must contain only scheme, host and port.");
            foreach (var attribute in new[] { directory.EntryIdAttribute, directory.PrincipalAttribute, directory.LinuxAccountAttribute, directory.EnabledAttribute })
                if (string.IsNullOrEmpty(attribute) || attribute.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                    throw new ArgumentException("Directory attribute names must be simple LDAP attribute names.");
            if (string.IsNullOrEmpty(directory.EnabledValue)) throw new ArgumentException("An explicit directory enabled value is required.");
            if ((directory.BindDistinguishedName is null) != (directory.BindPasswordFile is null))
                throw new ArgumentException("Directory bind DN and private password file must be configured together.");
        }
    }

    public DirectoryIdentityState Check(EimMapping mapping)
    {
        if (!_directories.TryGetValue(mapping.Directory, out var directory)) return DirectoryIdentityState.Unavailable;
        try
        {
            var uri = new Uri(directory.Uri);
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(uri.Host,
                uri.IsDefaultPort ? (uri.Scheme == "ldaps" ? 636 : 389) : uri.Port));
            connection.Timeout = TimeSpan.FromSeconds(5);
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.SessionOptions.SecureSocketLayer = uri.Scheme == "ldaps";
            if (directory.TrustedCertificatesDirectory is { } certificates && OperatingSystem.IsLinux())
            {
                connection.SessionOptions.TrustedCertificatesDirectory = certificates;
                connection.SessionOptions.StartNewTlsSessionContext();
            }
            if (directory.BindDistinguishedName is { } bindDn)
            {
                var path = directory.BindPasswordFile!;
                if (new FileInfo(path).Length > 4096 || new FileInfo(path).LinkTarget is not null || !OperatingSystem.IsWindows() &&
                    (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                    return DirectoryIdentityState.Unavailable;
                connection.AuthType = AuthType.Basic;
                var password = File.ReadAllText(path).TrimEnd('\r', '\n');
                if (password.Length == 0) return DirectoryIdentityState.Unavailable;
                connection.Credential = new NetworkCredential(bindDn, password);
            }
            else connection.AuthType = AuthType.Anonymous;
            connection.Bind();
            var attributes = new[] { directory.EntryIdAttribute, directory.PrincipalAttribute, directory.LinuxAccountAttribute, directory.EnabledAttribute };
            var request = new SearchRequest(mapping.DistinguishedName, "(objectClass=*)", SearchScope.Base, attributes)
            { TimeLimit = TimeSpan.FromSeconds(5), SizeLimit = 2 };
            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0) return DirectoryIdentityState.Missing;
            if (response.Entries.Count != 1) return DirectoryIdentityState.IdentityMismatch;
            var entry = response.Entries[0];
            string[] Values(string attribute) => entry.Attributes[attribute]?.GetValues(typeof(string)).Cast<string>().ToArray() ?? Array.Empty<string>();
            if (!Values(directory.EntryIdAttribute).SequenceEqual(new[] { mapping.EntryId }) ||
                !Values(directory.LinuxAccountAttribute).SequenceEqual(new[] { mapping.LinuxAccount }) ||
                !Values(directory.PrincipalAttribute).Contains(mapping.Principal, StringComparer.Ordinal))
                return DirectoryIdentityState.IdentityMismatch;
            return Values(directory.EnabledAttribute).SequenceEqual(new[] { directory.EnabledValue })
                ? DirectoryIdentityState.Active : DirectoryIdentityState.Disabled;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        { return DirectoryIdentityState.Missing; }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or DllNotFoundException)
        { return DirectoryIdentityState.Unavailable; }
    }
}
