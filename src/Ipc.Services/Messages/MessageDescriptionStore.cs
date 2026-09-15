using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Messages;

/// <summary>Versioned message-description text used by display compilation and validation.</summary>
public sealed class MessageDescriptionStore(SqliteConnectionFactory factory)
{
    private sealed record Payload(int Version, Dictionary<string, string> Messages);
    public void Create(string library, string name, string? description = null)
    {
        try { new SqliteObjectStore(factory).Create(new ObjectDescriptor {
        Key = new(library, name), ObjectType = ObjectType.MessageFile, Description = description,
        Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", Source = JsonSerializer.Serialize(new Payload(1, new())) }); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode == 1555 || error.SqliteExtendedErrorCode == 2067)
        { throw new CpfException("CPF7302", "Message file already exists."); }
    }
    public void Add(string library, string name, string id, string text)
    {
        if (id.Length != 7 || !id.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) || text.Length is < 1 or > 512 || text.Any(char.IsControl))
            throw new CpfException("IPC0127", "Message IDs require seven uppercase letters/digits; message text requires 1–512 printable characters.");
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject(library, name, ObjectType.MessageFile, AuthorityBit.ObjectManagement);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        authorization.RequireObject(library, name, ObjectType.MessageFile, AuthorityBit.ObjectManagement);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source FROM sys_objects WHERE lib=$lib AND name=$name AND type='*MSGF'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        var payload = Parse(command.ExecuteScalar() as string);
        if (payload.Messages.Count >= 4096) throw new CpfException("IPC0127", "Message file limit is 4096 descriptions.");
        if (!payload.Messages.TryAdd(id, text)) throw new CpfException("CPF2412", "Message description already exists.");
        command.CommandText = "UPDATE sys_objects SET source=$source,changed=$now WHERE lib=$lib AND name=$name AND type='*MSGF'";
        command.Parameters.AddWithValue("$source", JsonSerializer.Serialize(payload)); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public string Get(string library, string name, string id)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.MessageFile);
        return Parse(descriptor.Source).Messages.GetValueOrDefault(id) ?? throw new CpfException("CPF2401", "Message description not found.");
    }
    private static Payload Parse(string? json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(json ?? "");
            return payload is { Version: 1, Messages: not null } ? payload : throw new CpfException("IPC0127", "Unsupported message-file payload.");
        }
        catch (JsonException) { throw new CpfException("IPC0127", "Invalid message-file payload."); }
    }
}
