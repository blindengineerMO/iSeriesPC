using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Catalog;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

public sealed record SignedObjectPayload(string Library, string Name, string Type, string Owner, DateTimeOffset Created,
    string? Description, int Ccsid, string? Attribute, string? Format, AuthorityBit PublicAuthority, string Source,
    IReadOnlyDictionary<string, string> Attributes);
public sealed record SignedObjectPackage(int Version, string Resource, int CatalogVersion,
    IReadOnlyList<SignedObjectPayload> Objects, string? Service = null);
public sealed record SignedDeploymentResult(string ArtifactId, int ObjectCount, string? Service);

/// <summary>Actual catalog admission boundaries for signed code restores, updates and service publication.
/// Full data archives, operating-system updates and IWS codecs extend these boundaries in their owning work packages.</summary>
public sealed class SignedDeploymentService(SqliteConnectionFactory factory, ContentTrustService trust, ObjectSigningService signing)
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public byte[] Export(string resource, IReadOnlyList<QualifiedObject> objects, string? service = null)
    {
        Admin();
        if (objects.Count is < 1 or > 1000) throw Invalid("Package must contain 1 through 1000 objects.");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var payloads = objects.Select(key =>
        {
            new ServiceAuthorization(factory).RequireObject(key.Library, key.Name, key.Type, Authorities.UseBits);
            var descriptor = ObjectSigningService.Read(connection, transaction, key.Library, key.Name, key.Type);
            ValidateDescriptor(descriptor);
            signing.RequireExecutable(descriptor, connection, transaction);
            return Payload(descriptor);
        }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SignedObjectPackage(1, resource, SystemCatalog.SchemaVersion, payloads, service), Json);
        if (bytes.Length > ContentTrustService.MaximumContentBytes) throw Invalid("Package exceeds 64 MiB.");
        return bytes;
    }

    public SignedDeploymentResult RestoreObjects(ReadOnlySpan<byte> content, ObjectSignature signature) => Apply(content, signature, CertificatePurpose.Restore);
    public SignedDeploymentResult ApplyObjectUpdate(ReadOnlySpan<byte> content, ObjectSignature signature) => Apply(content, signature, CertificatePurpose.Update);
    public SignedDeploymentResult DeployService(ReadOnlySpan<byte> content, ObjectSignature signature) => Apply(content, signature, CertificatePurpose.ServiceDeployment);

    public QualifiedObject ResolveService(string name)
    {
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT d.lib,d.program,a.resource,a.content,a.signature FROM sys_service_deployments d
            JOIN sys_verified_artifacts a ON a.id=d.artifact_id WHERE d.name=$name AND a.purpose='ServiceDeployment'
            """;
        command.Parameters.AddWithValue("$name", name);
        QualifiedObject key; byte[] content; string resource; ObjectSignature signature;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw Invalid("Published service not found.");
            key = new(reader.GetString(0), reader.GetString(1), ObjectType.Program);
            resource = reader.GetString(2); content = (byte[])reader[3];
            signature = JsonSerializer.Deserialize<ObjectSignature>(reader.GetString(4), Json) ?? throw Invalid("Service signature is absent.");
        }
        new ServiceAuthorization(factory).RequireObject(key.Library, key.Name, key.Type, Authorities.UseBits);
        trust.Verify(content, CertificatePurpose.ServiceDeployment, resource, signature);
        var descriptor = ObjectSigningService.Read(connection, transaction, key.Library, key.Name, key.Type);
        signing.RequireExecutable(descriptor, connection, transaction);
        var package = Parse(content);
        var published = Descriptor(package.Objects[0]);
        if (!ObjectSigningService.Canonical(descriptor, connection, transaction).AsSpan().SequenceEqual(
            ObjectSigningService.Canonical(published, connection, transaction))) throw Invalid("Published program changed; deploy its signed package again.");
        return key;
    }

    private SignedDeploymentResult Apply(ReadOnlySpan<byte> input, ObjectSignature signature, CertificatePurpose purpose)
    {
        Admin();
        // Own the bytes throughout validation and mutation; callers cannot alter a shared buffer after verification.
        if (input.Length > ContentTrustService.MaximumContentBytes) throw Invalid("Package exceeds 64 MiB.");
        var content = input.ToArray();
        var package = Parse(content);
        if (package.Version != 1 || package.CatalogVersion != SystemCatalog.SchemaVersion)
            throw Invalid("Package format or catalog version is incompatible.");
        if (package.Objects is null || package.Objects.Count is < 1 or > 1000) throw Invalid("Package object count is invalid.");
        if (purpose == CertificatePurpose.ServiceDeployment)
        {
            if (package.Service is null || !ObjectName.IsValid(package.Service) || package.Objects.Count != 1 || package.Objects[0].Type != ObjectType.Program)
                throw Invalid("Service deployment requires a service name and exactly one program.");
        }
        else if (package.Service is not null) throw Invalid("Only service deployment can publish a service.");
        var descriptors = package.Objects.Select(Descriptor).ToArray();
        if (descriptors.Select(d => (d.Library, d.Name, d.ObjectType)).Distinct().Count() != descriptors.Length)
            throw Invalid("Package contains duplicate object identities.");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        trust.Verify(content, purpose, package.Resource, signature);
        var creates = new HashSet<ObjectDescriptor>();
        foreach (var descriptor in descriptors)
        {
            ValidateDescriptor(descriptor);
            // Each executable has its own object-purpose signature, independently of the transport/package signer.
            signing.RequireExecutable(descriptor, connection, transaction);
            var authorization = new ServiceAuthorization(factory);
            using var existing = connection.CreateCommand(); existing.Transaction = transaction;
            existing.CommandText = "SELECT owner FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type";
            Bind(existing, descriptor);
            var owner = existing.ExecuteScalar() as string;
            if (descriptor.ObjectType == ObjectType.Program && descriptor.Attribute == Ipc.Services.Work.ExternalProgramService.Attribute)
                authorization.RequireSpecial(SpecialAuthority.Service, allowAdopted: false);
            if (owner is null) authorization.RequireCreate(descriptor);
            else
            {
                if (purpose == CertificatePurpose.Restore) throw Invalid("Signed code restore does not replace an existing object.");
                authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectManagement);
                if (owner != descriptor.Owner) authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectExist);
                authorization.RequireAdoption(descriptor);
            }
            if (owner is null) creates.Add(descriptor);
        }
        foreach (var descriptor in descriptors.Where(d => d.ObjectType == ObjectType.Command))
            new Ipc.Services.Commands.CommandDefinitionStore(factory).AuthorizeDependencies(Ipc.Services.Commands.CommandDefinitionStore.ValidatePayload(descriptor), connection, transaction, descriptors);
        foreach (var descriptor in descriptors)
        {
            if (creates.Contains(descriptor)) new SqliteObjectStore(factory).CreateAuthorized(descriptor, connection, transaction);
            else
            {
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = """
                    UPDATE sys_objects SET owner=$owner,created=$created,changed=$now,description=$description,ccsid=$ccsid,
                      attribute=$attribute,format=$format,public_authority=$authority,source=$source,attrs=$attrs
                    WHERE lib=$lib AND name=$name AND type=$type
                    """;
                Bind(update, descriptor);
                update.Parameters.AddWithValue("$owner", descriptor.Owner); update.Parameters.AddWithValue("$created", descriptor.Created.ToString("o"));
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o")); update.Parameters.AddWithValue("$description", (object?)descriptor.Description ?? DBNull.Value);
                update.Parameters.AddWithValue("$ccsid", descriptor.Ccsid); update.Parameters.AddWithValue("$attribute", (object?)descriptor.Attribute ?? DBNull.Value);
                update.Parameters.AddWithValue("$format", (object?)descriptor.Format ?? DBNull.Value); update.Parameters.AddWithValue("$authority", (long)descriptor.PublicAuthority);
                update.Parameters.AddWithValue("$source", descriptor.Source!); update.Parameters.AddWithValue("$attrs", JsonSerializer.Serialize(descriptor.ExtendedAttributes));
                update.ExecuteNonQuery();
            }
            using var pin = connection.CreateCommand(); pin.Transaction = transaction;
            pin.CommandText = "INSERT INTO sys_object_signature_policy(lib,name,type) VALUES($lib,$name,$type) ON CONFLICT DO NOTHING";
            Bind(pin, descriptor); pin.ExecuteNonQuery();
        }
        foreach (var descriptor in descriptors.Where(d => d.ObjectType == ObjectType.Command))
        {
            var definition = Ipc.Services.Commands.CommandDefinitionStore.ValidatePayload(descriptor);
            new Ipc.Services.Commands.CommandDefinitionStore(factory).BindDependencies(descriptor, definition, connection, transaction);
        }
        var id = Guid.NewGuid().ToString("N");
        using (var artifact = connection.CreateCommand())
        {
            artifact.Transaction = transaction;
            artifact.CommandText = "INSERT INTO sys_verified_artifacts(id,purpose,resource,content,signature,admitted,principal) VALUES($id,$purpose,$resource,$content,$signature,$now,$principal)";
            artifact.Parameters.AddWithValue("$id", id); artifact.Parameters.AddWithValue("$purpose", purpose.ToString());
            artifact.Parameters.AddWithValue("$resource", package.Resource); artifact.Parameters.AddWithValue("$content", content);
            artifact.Parameters.AddWithValue("$signature", JsonSerializer.Serialize(signature)); artifact.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            artifact.Parameters.AddWithValue("$principal", OperationIdentity.Current?.Principal ?? "*SYSTEM"); artifact.ExecuteNonQuery();
        }
        if (purpose == CertificatePurpose.ServiceDeployment)
        {
            using var publish = connection.CreateCommand(); publish.Transaction = transaction;
            publish.CommandText = """
                INSERT INTO sys_service_deployments(name,lib,program,artifact_id,changed) VALUES($name,$lib,$program,$artifact,$now)
                ON CONFLICT(name) DO UPDATE SET lib=excluded.lib,program=excluded.program,artifact_id=excluded.artifact_id,changed=excluded.changed
                """;
            publish.Parameters.AddWithValue("$name", package.Service!); publish.Parameters.AddWithValue("$lib", descriptors[0].Library);
            publish.Parameters.AddWithValue("$program", descriptors[0].Name); publish.Parameters.AddWithValue("$artifact", id);
            publish.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o")); publish.ExecuteNonQuery();
        }
        MfaStore.Audit(connection, transaction, "security.signed-deployment." + purpose.ToString().ToLowerInvariant(),
            OperationIdentity.Current?.Principal ?? "*SYSTEM", true, DateTimeOffset.UtcNow);
        transaction.Commit(); return new(id, descriptors.Length, package.Service);
    }

    private static SignedObjectPackage Parse(ReadOnlySpan<byte> content)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            CheckUniqueFields(document.RootElement);
            var package = document.RootElement.Deserialize<SignedObjectPackage>(Json) ?? throw Invalid("Package is empty.");
            if (package.Resource is not { Length: > 0 and <= 256 } || package.Resource.Any(char.IsControl) ||
                package.Objects is null || package.Objects.Any(p => p is null || p.Library is null || p.Name is null ||
                    p.Type is null || p.Owner is null || p.Source is null || p.Attributes is null || p.Attributes.Any(a => a.Value is null)))
                throw Invalid("Required package fields are absent or invalid.");
            return package;
        }
        catch (JsonException) { throw Invalid("Package JSON is invalid."); }
    }

    private static void CheckUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid("Duplicate JSON properties are not allowed in signed packages.");
                CheckUniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckUniqueFields(child);
    }
    private static void ValidateDescriptor(ObjectDescriptor descriptor)
    {
        descriptor.ValidateIdentity();
        if (descriptor.ObjectType == ObjectType.Command) _ = Ipc.Services.Commands.CommandDefinitionStore.ValidatePayload(descriptor);
        if (descriptor.ObjectType is not (ObjectType.Program or ObjectType.Module or ObjectType.ServiceProgram or ObjectType.Command or ObjectType.PanelGroup or ObjectType.Documentation or ObjectType.QueryManagementQuery))
            throw Invalid("Signed code packages cannot carry this object's separate domain payload.");
        if (string.IsNullOrEmpty(descriptor.Source) || System.Text.Encoding.UTF8.GetByteCount(descriptor.Source) > 4194304 || descriptor.Signature is null)
            throw Invalid("Code package objects require source of at most 4 MiB and an object signature.");
    }
    private static SignedObjectPayload Payload(ObjectDescriptor d) => new(d.Library, d.Name, d.ObjectType, d.Owner, d.Created,
        d.Description, d.Ccsid, d.Attribute, d.Format, d.PublicAuthority, d.Source!, new Dictionary<string, string>(d.ExtendedAttributes!));
    private static ObjectDescriptor Descriptor(SignedObjectPayload p) => new() { Key = new(p.Library, p.Name), ObjectType = p.Type,
        Owner = p.Owner, Created = p.Created, Description = p.Description, Ccsid = p.Ccsid, Attribute = p.Attribute, Format = p.Format,
        PublicAuthority = p.PublicAuthority, Source = p.Source, ExtendedAttributes = p.Attributes };
    private static void Bind(SqliteCommand command, ObjectDescriptor d)
    { command.Parameters.AddWithValue("$lib", d.Library); command.Parameters.AddWithValue("$name", d.Name); command.Parameters.AddWithValue("$type", d.ObjectType); }
    private void Admin() => new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator, allowAdopted: false);
    private static CpfException Invalid(string message) => CertificateService.Invalid(message);
}

public sealed record QualifiedObject(string Library, string Name, string Type);
