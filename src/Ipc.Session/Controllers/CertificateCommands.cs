using System.Security.Cryptography;
using System.Text.Json;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Security;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterCertificateCommands()
    {
        _catalog.Register("WRKCERT", ExecuteCertificate);
        _catalog.Register("CRTLOCALCA", ExecuteCertificate);
        _catalog.Register("CRTLOCCERT", ExecuteCertificate);
        _catalog.Register("IMPCERT", ExecuteCertificate);
        _catalog.Register("TRUSTCERT", ExecuteCertificate);
        _catalog.Register("RVKCERT", ExecuteCertificate);
        _catalog.Register("RNWCERT", ExecuteCertificate);
        _catalog.Register("BINDCERT", ExecuteCertificate);
        _catalog.Register("WRKKEYRING", ExecuteCertificate);
        _catalog.Register("ROTKEYRING", ExecuteCertificate);
        _catalog.Register("SGNOBJ", ExecuteCertificate);
        _catalog.Register("CHKOBJITG", ExecuteCertificate);
        _catalog.Register("RSTSGNOBJ", ExecuteCertificate);
        _catalog.Register("APYSGNUPD", ExecuteCertificate);
        _catalog.Register("DPLYSGNSRV", ExecuteCertificate);
    }

    private CommandResult ExecuteCertificate(CommandCall call)
    {
        string Value(string name, string fallback = "") => CommandParser.Unquote(call.GetOption(name) ?? fallback);
        CertificatePurpose Purpose() => Enum.TryParse<CertificatePurpose>(Value("PURPOSE", "ObjectSigning"), true, out var purpose) && Enum.IsDefined(purpose)
            ? purpose : throw new CpfException("IPC0003", "Unknown certificate purpose.");
        int Days(int fallback) => int.TryParse(Value("DAYS", fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)), out var days)
            ? days : throw new CpfException("IPC0003", "DAYS must be an integer.");
        string[] Dns() => call.Split("DNS").Select(CommandParser.Unquote).ToArray();
        var certificates = _system.Certificates;
        var id = Value("CERT").ToUpperInvariant();
        try
        {
            switch (call.Name.ToUpperInvariant())
            {
                case "WRKCERT":
                    return new CommandResult { Listing = certificates.List().Select(c =>
                        $"{c.Label} {c.State} expires={c.NotAfter:u} days={c.DaysRemaining} private={c.HasPrivateKey}\n  {c.Id}\n  {c.Subject}").ToArray() };
                case "WRKKEYRING":
                    return new CommandResult { Listing = certificates.WrappingKeys().Select(k => $"{k.Id} active={k.Active} encrypted-values={k.EncryptedValues}").ToArray() };
                case "ROTKEYRING": return CommandResult.Ok("Wrapping key rotated: " + certificates.RotateWrappingKey());
                case "CRTLOCALCA":
                    return CommandResult.Ok("CA created; trust must be explicitly configured: " + certificates.CreateAuthority(Value("LABEL"), Value("CN"), Days(3650)).Id);
                case "CRTLOCCERT":
                    return CommandResult.Ok("Certificate issued: " + certificates.Issue(Value("LABEL"), Value("CN"), Value("CA").ToUpperInvariant(), Purpose(), Days(365), Dns()).Id);
                case "IMPCERT":
                    var encoded = ReadCertificateFile(Value("FILE"), 1048576, privateOnly: true);
                    try
                    {
                        var password = Value("PWDFILE") is { Length: > 0 } path ? System.Text.Encoding.UTF8.GetString(ReadCertificateFile(path, 4096, privateOnly: true)).TrimEnd('\r', '\n') : null;
                        return CommandResult.Ok("Certificate imported: " + certificates.Import(Value("LABEL"), encoded, password).Id);
                    }
                    finally { CryptographicOperations.ZeroMemory(encoded); }
                case "TRUSTCERT":
                    var trust = Value("TRUST", "*YES").ToUpperInvariant();
                    if (trust is not ("*YES" or "*NO")) throw new CpfException("IPC0003", "TRUST must be *YES or *NO.");
                    certificates.SetTrust(id, Purpose(), trust == "*YES"); return CommandResult.Ok("Certificate trust updated.");
                case "RVKCERT": certificates.Revoke(id); return CommandResult.Ok("Certificate revoked.");
                case "RNWCERT": return CommandResult.Ok("Renewed certificate: " + certificates.Renew(id, Value("CA").ToUpperInvariant(), Purpose(), Days(365), Dns()).Id);
                case "BINDCERT": certificates.Bind(Value("SERVICE"), id, Purpose()); return CommandResult.Ok("Certificate binding updated.");
                case "SGNOBJ":
                    var type = Value("OBJTYPE", "*PGM").ToUpperInvariant();
                    var (library, name) = ResolveSignedObject(Value("OBJ"), type);
                    _system.ObjectSigning.Sign(library, name, type, id);
                    return CommandResult.Ok($"Signed {library}/{name} {type}.");
                case "CHKOBJITG": return CheckIntegrity(call);
                case "RSTSGNOBJ":
                case "APYSGNUPD":
                case "DPLYSGNSRV":
                    var package = ReadCertificateFile(Value("FILE"), ContentTrustService.MaximumContentBytes);
                    var signature = JsonSerializer.Deserialize<ObjectSignature>(ReadCertificateFile(Value("SIGFILE"), 16384))
                        ?? throw new CpfException("IPC0110", "Signature file is empty.");
                    var deployed = call.Name.ToUpperInvariant() switch
                    {
                        "RSTSGNOBJ" => _system.SignedDeployments.RestoreObjects(package, signature),
                        "APYSGNUPD" => _system.SignedDeployments.ApplyObjectUpdate(package, signature),
                        _ => _system.SignedDeployments.DeployService(package, signature),
                    };
                    return CommandResult.Ok($"Verified artifact {deployed.ArtifactId}; {deployed.ObjectCount} code object(s) installed.");
                default: return CommandResult.Error("IPC0002: Certificate operation is not implemented.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        { return CommandResult.Error("IPC0110: Certificate, protected key, package or signature could not be read or verified."); }
    }

    private CommandResult CheckIntegrity(CommandCall call)
    {
        new ServiceAuthorization(_system.Connections).RequireSpecial(SpecialAuthority.Audit);
        var owner = CommandParser.Unquote(call.GetOption("USRPRF")).ToUpperInvariant();
        var path = CommandParser.Unquote(call.GetOption("OBJ")).ToUpperInvariant();
        var check = CommandParser.Unquote(call.GetOption("CHKSIG") ?? "*SIGNED").ToUpperInvariant();
        if (check is not ("*SIGNED" or "*ALL")) throw new CpfException("IPC0003", "This signature checker supports CHKSIG(*SIGNED|*ALL).");
        if ((owner.Length == 0) == (path.Length == 0)) throw new CpfException("IPC0003", "Specify exactly one of USRPRF or OBJ.");
        var keys = new List<QualifiedObject>();
        if (path.Length > 0 && path != "*SYSTEM")
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "QSYS.LIB" || !parts[1].EndsWith(".LIB", StringComparison.Ordinal) || !parts[2].Contains('.'))
                throw new CpfException("IPC0003", "OBJ must be /QSYS.LIB/LIBRARY.LIB/OBJECT.TYPE or *SYSTEM.");
            var dot = parts[2].LastIndexOf('.');
            keys.Add(new(parts[1][..^4], parts[2][..dot], "*" + parts[2][(dot + 1)..]));
        }
        else
        {
            if (path == "*SYSTEM" && check != "*ALL") throw new CpfException("IPC0003", "OBJ(*SYSTEM) requires CHKSIG(*ALL).");
            using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT lib,name,type,owner FROM sys_objects ORDER BY lib,name,type";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (owner is "" or "*ALL" || (owner.EndsWith('*') ? reader.GetString(3).StartsWith(owner[..^1], StringComparison.Ordinal) : reader.GetString(3) == owner))
                    keys.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        var results = keys.Select(key => _system.ObjectSigning.Check(key.Library, key.Name, key.Type, check == "*ALL")).ToArray();
        var violations = results.Count(r => r.Result is not ("VALID" or "UNSIGNED" or "NOTCHECKED"));
        return new CommandResult { Outcome = violations > 0 ? CommandOutcome.Error : CommandOutcome.Continue,
            Message = $"{results.Length} object(s) checked; {violations} signature violation(s). Results retained in the integrity catalog.",
            Listing = results.Select(r => $"{r.Library}/{r.Name} {r.Type} {r.Result}").ToArray() };
    }

    private (string Library, string Name) ResolveSignedObject(string value, string type)
    {
        var (library, name) = SplitQualified(value, "*LIBL");
        if (library is "*LIBL" or "*CURLIB") library = SearchLibraries(library).FirstOrDefault(l => _system.Objects.Exists(l, name, type))
            ?? throw new CpfException("CPF9801", "Object not found.");
        return (library, name);
    }

    private byte[] ReadCertificateFile(string path, int maximum, bool privateOnly = false)
    {
        var authorization = new ServiceAuthorization(_system.Connections);
        authorization.RequireSpecial(SpecialAuthority.SecurityAdministrator, allowAdopted: false);
        authorization.RequireSpecial(SpecialAuthority.Service, allowAdopted: false);
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length is < 1 || file.Length > maximum)
            throw new CpfException("IPC0110", "File is missing, linked or exceeds its size limit.");
        if (privateOnly && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            throw new CpfException("IPC0110", "Certificate/key/password imports must use private owner-only files.");
        using var stream = file.OpenRead();
        var bytes = new byte[(int)file.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new CpfException("IPC0110", "File changed during import.");
        return bytes;
    }
}
