using System.Security.Cryptography;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

public sealed record ObjectIntegrityResult(string Library, string Name, string Type, string Result, string? Detail = null);

public sealed class ObjectSigningService(SqliteConnectionFactory factory, ContentTrustService trust)
{
    public static bool Supports(string type) => type is ObjectType.Program or ObjectType.Module or ObjectType.ServiceProgram or
        ObjectType.Command or ObjectType.PanelGroup or ObjectType.Menu or ObjectType.Documentation or ObjectType.QueryManagementQuery;

    public ObjectSignature Sign(string library, string name, string type, string certificateId)
    {
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireSpecial(SpecialAuthority.SecurityAdministrator, allowAdopted: false);
        authorization.RequireObject(library, name, type, AuthorityBit.ObjectManagement);
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var descriptor = Read(connection, transaction, library, name, type);
        if (!Supports(type)) throw CertificateService.Invalid("This object payload is not supported for signing.");
        var bytes = Canonical(descriptor, connection, transaction);
        var signature = trust.Sign(bytes, CertificatePurpose.ObjectSigning, Resource(descriptor), certificateId);
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE sys_objects SET attrs=json_set(coalesce(attrs,'{}'),'$."ipc.signature"', $signature),changed=$now
            WHERE lib=$lib AND name=$name AND type=$type;
            INSERT INTO sys_object_signature_policy(lib,name,type) VALUES($lib,$name,$type) ON CONFLICT DO NOTHING;
            """;
        Bind(update, library, name, type);
        update.Parameters.AddWithValue("$signature", JsonSerializer.Serialize(signature));
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o")); update.ExecuteNonQuery();
        MfaStore.Audit(connection, transaction, "security.object.signed", OperationIdentity.Current?.Principal ?? "*SYSTEM", true, DateTimeOffset.UtcNow);
        transaction.Commit(); return signature;
    }

    public ObjectIntegrityResult Check(string library, string name, string type, bool requireSignature = true)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.Audit);
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var descriptor = Read(connection, transaction, library, name, type);
        var result = Evaluate(descriptor, connection, transaction, requireSignature);
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO sys_integrity_results(at,principal,lib,name,type,result) VALUES($now,$principal,$lib,$name,$type,$result)";
        Bind(insert, library, name, type);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        insert.Parameters.AddWithValue("$principal", OperationIdentity.Current?.Principal ?? "*SYSTEM");
        insert.Parameters.AddWithValue("$result", result.Result); insert.ExecuteNonQuery();
        MfaStore.Audit(connection, transaction, "security.object.integrity", OperationIdentity.Current?.Principal ?? "*SYSTEM",
            result.Result is "VALID" or "UNSIGNED", DateTimeOffset.UtcNow);
        transaction.Commit(); return result;
    }

    // Called with the exact descriptor snapshot whose source the runtime will compile.
    public void RequireExecutable(ObjectDescriptor descriptor)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireExecutable(descriptor, connection, transaction);
    }

    internal void RequireExecutable(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var policy = connection.CreateCommand(); policy.Transaction = transaction;
        policy.CommandText = "SELECT count(*) FROM sys_object_signature_policy WHERE lib=$lib AND name=$name AND type=$type";
        Bind(policy, descriptor.Library, descriptor.Name, descriptor.ObjectType);
        var required = Convert.ToInt64(policy.ExecuteScalar()) != 0;
        var result = Evaluate(descriptor, connection, transaction, required);
        if (result.Result is not ("VALID" or "UNSIGNED")) throw CertificateService.Invalid("Object integrity failed: " + result.Result);
    }

    private ObjectIntegrityResult Evaluate(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction transaction, bool required)
    {
        ObjectIntegrityResult Result(string outcome, string? detail = null) => new(descriptor.Library, descriptor.Name, descriptor.ObjectType, outcome, detail);
        if (!Supports(descriptor.ObjectType)) return Result("NOTCHECKED", "Object payload does not have a signing codec.");
        try
        {
            var signature = descriptor.Signature;
            if (signature is null) return Result(required ? "NOSIG" : "UNSIGNED");
            var content = Canonical(descriptor, connection, transaction);
            if (Convert.ToHexString(SHA256.HashData(content)) != signature.ContentHash) return Result("ALTERED");
            trust.Verify(content, CertificatePurpose.ObjectSigning, Resource(descriptor), signature);
            return Result("VALID");
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or CpfException or FormatException)
        { return Result("UNTRUSTED", "Signature format, certificate validity or purpose trust failed."); }
    }

    internal static byte[] Canonical(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1);
            writer.WriteString("library", descriptor.Library); writer.WriteString("name", descriptor.Name);
            writer.WriteString("type", descriptor.ObjectType); writer.WriteString("owner", descriptor.Owner);
            writer.WriteString("created", descriptor.Created.ToUniversalTime().ToString("o"));
            writer.WriteString("description", descriptor.Description); writer.WriteNumber("ccsid", descriptor.Ccsid);
            writer.WriteString("attribute", descriptor.Attribute); writer.WriteString("format", descriptor.Format);
            writer.WriteNumber("publicAuthority", (long)descriptor.PublicAuthority); writer.WriteString("source", descriptor.Source);
            writer.WriteStartObject("attributes");
            foreach (var pair in (descriptor.ExtendedAttributes ?? new Dictionary<string, string>()).Where(p => p.Key != "ipc.signature").OrderBy(p => p.Key, StringComparer.Ordinal))
                writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
            if (descriptor.ObjectType == ObjectType.Menu)
            {
                using var menu = connection.CreateCommand(); menu.Transaction = transaction;
                menu.CommandText = "SELECT title FROM sys_menus WHERE library=$lib AND name=$name";
                Bind(menu, descriptor.Library, descriptor.Name, descriptor.ObjectType);
                writer.WriteString("menuTitle", menu.ExecuteScalar() as string);
                menu.CommandText = "SELECT ordinal,number,text,target,kind FROM sys_menu_options WHERE library=$lib AND menu=$name ORDER BY ordinal";
                writer.WriteStartArray("options");
                using var reader = menu.ExecuteReader();
                while (reader.Read())
                {
                    writer.WriteStartArray();
                    for (var i = 0; i < 5; i++) JsonSerializer.Serialize(writer, reader.IsDBNull(i) ? null : reader.GetValue(i));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        if (stream.Length > ContentTrustService.MaximumContentBytes) throw CertificateService.Invalid("Object is too large to sign.");
        return stream.ToArray();
    }

    internal static ObjectDescriptor Read(SqliteConnection connection, SqliteTransaction transaction, string library, string name, string type)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT * FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type";
        Bind(command, library, name, type); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new CpfException("CPF9801", "Object not found.");
        return SqliteObjectStore.ReadDescriptor(reader);
    }
    private static void Bind(SqliteCommand command, string library, string name, string type)
    { command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$type", type); }
    private static string Resource(ObjectDescriptor descriptor) => $"{descriptor.Library}/{descriptor.Name}:{descriptor.ObjectType}";
}
