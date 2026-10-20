using System.Security.Cryptography;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Publish a fully validated replacement only if the original object is still the one inspected.</summary>
public sealed class RpgObjectUpdateStore(SqliteConnectionFactory factory, ObjectSigningService signing)
{
    public void Authorize(ObjectDescriptor descriptor)
    {
        if (descriptor.ObjectType is not (ObjectType.Program or ObjectType.ServiceProgram)) throw new CpfException("CPF5D1B", "Only bound programs and service programs can be updated.");
        if (descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.program.allowUpdate") is { } allow && allow != "*YES") throw new CpfException("CPF5D1B", "Updates are not allowed for this object.");
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject("QSYS", descriptor.Library, ObjectType.Library, Authorities.UseBits | AuthorityBit.Add, checkLibrary: false);
        authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, Authorities.UseBits);
        authorization.RequireAdoption(descriptor);
        authorization.RequireObjectOwner(descriptor);
    }
    public static string? UpdatedActivationGroup(ObjectDescriptor descriptor, string requested)
    {
        requested = requested.ToUpperInvariant();
        if (requested == "*SAME") return null;
        var current = descriptor.ExtendedAttributes?.GetValueOrDefault("ipc.program.activationGroup") ?? "*CALLER";
        if (!ObjectName.IsValid(requested) || !ObjectName.IsValid(current))
            throw new CpfException("IPC0124", "Only a named activation group can be changed to another name.");
        return requested;
    }
    public void Publish(ObjectDescriptor expected, string source, string attribute, CancellationToken cancellationToken = default, string? activationGroup = null)
    {
        var objects = new SqliteObjectStore(factory); var live = objects.GetRequired(expected.Library, expected.Name, expected.ObjectType); Authorize(live);
        var key = new JobTemporaryLibrary(factory).Resolve(live.Key, live.ObjectType);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        ObjectDescriptor current;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT * FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type";
            read.Parameters.AddWithValue("$lib", key.Library); read.Parameters.AddWithValue("$name", key.Name.Value); read.Parameters.AddWithValue("$type", live.ObjectType);
            using var reader = read.ExecuteReader(); current = reader.Read() ? SqliteObjectStore.ReadDescriptor(reader) : throw new CpfException("CPF9801", "Object no longer exists.");
        }
        var original = expected.Key == key ? expected : expected.Snapshot(key);
        if (current.Changed != original.Changed || !SHA256.HashData(ObjectSigningService.Canonical(current, connection, transaction)).SequenceEqual(SHA256.HashData(ObjectSigningService.Canonical(original, connection, transaction))) ||
            current.ExtendedAttributes?.GetValueOrDefault("ipc.signature") != original.ExtendedAttributes?.GetValueOrDefault("ipc.signature"))
            throw new CpfException("IPC0130", "Object changed during update; retry from the current object.");
        signing.RequireExecutable(current, connection, transaction);
        var attributes = new Dictionary<string, string>(current.ExtendedAttributes ?? new Dictionary<string, string>()); attributes.Remove("ipc.signature");
        if (activationGroup is not null && UpdatedActivationGroup(current, activationGroup) is { } group) attributes["ipc.program.activationGroup"] = group;
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE sys_objects SET source=$source,attribute=$attribute,attrs=$attrs,changed=$at WHERE lib=$lib AND name=$name AND type=$type";
        update.Parameters.AddWithValue("$source", source); update.Parameters.AddWithValue("$attribute", attribute); update.Parameters.AddWithValue("$attrs", JsonSerializer.Serialize(attributes));
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$lib", key.Library); update.Parameters.AddWithValue("$name", key.Name.Value); update.Parameters.AddWithValue("$type", live.ObjectType);
        cancellationToken.ThrowIfCancellationRequested();
        if (update.ExecuteNonQuery() != 1) throw new CpfException("CPF9801", "Object no longer exists."); transaction.Commit();
    }
}
