using System.Text.Json;
using Ipc.Cl.Compatibility;
using Ipc.Cl.Definitions;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Commands;

/// <summary>Command source and its validated metadata are one signed catalog payload.</summary>
public sealed class CommandDefinitionStore(SqliteConnectionFactory factory)
{
    public const string MetadataAttribute = "ipc.command.definition";
    public static IReadOnlyList<string> BuiltinNames => BuiltinContract.DefinedNames;
    public void SeedDefaults()
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        var objects = new SqliteObjectStore(factory);
        foreach (var name in BuiltinNames)
        {
            var existing = objects.GetForAuthorization("QSYS", name, ObjectType.Command);
            var metadata = BuiltinContract.Metadata(name)!;
            var definition = new CommandDefinition { Builtin = name, Title = name, MaximumPositional = metadata.MaximumPositional,
                Parameters = metadata.Parameters.Select(p => new ParameterDefinition { Keyword = p.Keyword, Type = "*RAW", Length = p.MaximumLength, Prompt = p.Prompt, Secret = p.Secret, Literal = p.Literal }).ToArray() };
            var json = definition.ToJson();
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
            if (existing is not null)
            {
                if (existing.Owner != "QSYS" || existing.Attribute != "BUILTIN" || existing.Signature is not null || RequiresSignature(name) || existing.Source is null ||
                    existing.ExtendedAttributes?.TryGetValue("ipc.command.seed", out var seeded) != true || seeded != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(existing.Source))) ||
                    !existing.ExtendedAttributes.TryGetValue(MetadataAttribute, out var oldMetadata) || oldMetadata != existing.Source || existing.Source == json) continue;
                existing.Source = json; existing.Description = name;
                var attributes = new Dictionary<string, string>(existing.ExtendedAttributes) { [MetadataAttribute] = json, ["ipc.command.seed"] = fingerprint }; existing.ExtendedAttributes = attributes;
                objects.Update(existing); continue;
            }
            objects.Create(new ObjectDescriptor { Key = new("QSYS", name), ObjectType = ObjectType.Command, Owner = "QSYS", Attribute = "BUILTIN", Source = json,
                Description = name, ExtendedAttributes = new Dictionary<string, string> { [MetadataAttribute] = json, ["ipc.command.seed"] = fingerprint } });
        }
    }
    private bool RequiresSignature(string name)
    {
        using var connection = factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sys_object_signature_policy WHERE lib='QSYS' AND name=$name AND type='*CMD'";
        query.Parameters.AddWithValue("$name", name); return Convert.ToInt64(query.ExecuteScalar()) != 0;
    }
    public void Create(string library, string name, string source, CommandDefinition definition, string owner, bool replace = false)
    {
        if (definition.Builtin is not null) throw new CpfException("IPC0136", "CRTCMD creates processing-program commands, not built-in adapters.");
        var descriptor = new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.Command, Owner = owner, Attribute = "CMD", Description = definition.Title, Source = source,
            ExtendedAttributes = new Dictionary<string, string> { [MetadataAttribute] = definition.ToJson() } };
        _ = ValidatePayload(descriptor);
        var authorization = new ServiceAuthorization(factory); var objects = new SqliteObjectStore(factory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var find = connection.CreateCommand(); find.Transaction = transaction;
        find.CommandText = "SELECT attribute FROM sys_objects WHERE lib=$lib AND name=$name AND type='*CMD'"; Bind(find, library, name);
        var attribute = find.ExecuteScalar() as string;
        AuthorizeDependencies(definition, connection, transaction);
        if (attribute is not null)
        {
            if (!replace) throw new CpfException("CPF7302", "Command already exists; specify REPLACE(*YES).");
            if (attribute == "BUILTIN") throw new CpfException("IPC0136", "Built-in command definitions cannot be replaced by CRTCMD.");
            authorization.RequireObject(library, name, ObjectType.Command, AuthorityBit.ObjectManagement);
            new Ipc.Services.Work.JobLockStore(factory).RequireObjectMutation(library, name, ObjectType.Command);
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE sys_objects SET source=$source,description=$title,changed=$now,attrs=json_set(json_remove(coalesce(attrs,'{}'),'$.\"ipc.signature\"'),'$.\"ipc.command.definition\"',$definition) WHERE lib=$lib AND name=$name AND type='*CMD'";
            Bind(update, library, name); update.Parameters.AddWithValue("$source", source); update.Parameters.AddWithValue("$title", definition.Title);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$definition", definition.ToJson()); update.ExecuteNonQuery();
        }
        else objects.Create(descriptor, connection, transaction);
        BindDependencies(descriptor, definition, connection, transaction);
        transaction.Commit();
    }
    internal void AuthorizeDependencies(CommandDefinition definition, SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ObjectDescriptor>? incoming = null)
    {
        foreach (var (key, type) in new[] { (definition.ProcessingProgram, ObjectType.Program), (definition.HelpPanelGroup, ObjectType.PanelGroup) })
        {
            if (key is null) continue; var target = QualifiedName.Parse(key);
            var descriptor = incoming?.SingleOrDefault(d => d.Library == target.Library && d.Name == target.Name.Value && d.ObjectType == type)
                ?? ObjectSigningService.Read(connection, transaction, target.Library, target.Name.Value, type);
            new ServiceAuthorization(factory).RequireSnapshot(descriptor, AuthorityBit.ObjectReference);
        }
    }
    internal void BindDependencies(ObjectDescriptor descriptor, CommandDefinition definition, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM sys_object_dependencies WHERE source_lib=$lib AND source_name=$name AND source_type='*CMD'"; Bind(command, descriptor.Library, descriptor.Name); command.ExecuteNonQuery();
        foreach (var (key, type) in new[] { (definition.ProcessingProgram, ObjectType.Program), (definition.HelpPanelGroup, ObjectType.PanelGroup) })
        {
            if (key is null) continue; var target = QualifiedName.Parse(key);
            using var dependency = connection.CreateCommand(); dependency.Transaction = transaction;
            dependency.CommandText = "INSERT INTO sys_object_dependencies VALUES($lib,$name,'*CMD',$tl,$tn,$tt)"; Bind(dependency, descriptor.Library, descriptor.Name);
            dependency.Parameters.AddWithValue("$tl", target.Library); dependency.Parameters.AddWithValue("$tn", target.Name.Value); dependency.Parameters.AddWithValue("$tt", type); dependency.ExecuteNonQuery();
        }
    }
    public CommandDefinition Load(string library, string name)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.Command);
        using var certificates = new CertificateService(factory);
        new ObjectSigningService(factory, new ContentTrustService(factory, certificates)).RequireExecutable(descriptor);
        return ValidatePayload(descriptor);
    }
    public static CommandDefinition ValidatePayload(ObjectDescriptor descriptor)
    {
        if (descriptor.ObjectType != ObjectType.Command || descriptor.Source is null || descriptor.ExtendedAttributes?.TryGetValue(MetadataAttribute, out var json) != true) throw new CpfException("IPC0136", "Command has no compiled definition.");
        var definition = CommandDefinition.FromJson(json ?? throw new CpfException("IPC0136", "Missing command metadata."));
        try
        {
            var compiled = definition.Builtin is null
                ? new CommandDefinitionCompiler().Compile(descriptor.Source, definition.ProcessingProgram!, descriptor.Key.ToString(), definition.HelpPanelGroup, definition.HelpId)
                : CommandDefinition.FromJson(descriptor.Source);
            if (compiled.ToJson() != definition.ToJson()) throw new CpfException("IPC0136", "Command source and metadata differ; compile the command again.");
            if (definition.Builtin is not null && (descriptor.Attribute != "BUILTIN" || descriptor.Library != "QSYS" || descriptor.Name != definition.Builtin)) throw new CpfException("IPC0136", "Invalid built-in command adapter.");
            return definition;
        }
        catch (ArgumentException) { throw new CpfException("IPC0136", "Stored command source is invalid."); }
    }
    private static void Bind(SqliteCommand command, string library, string name) { command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); }
}
