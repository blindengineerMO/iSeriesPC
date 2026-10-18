using System.Globalization;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JournalStore
{
    public IReadOnlyList<JournalEntry> Read(QualifiedName name, long fromSequence = 1, long? toSequence = null, int maximum = 1000, CancellationToken cancellationToken = default)
    {
        Require(name, ObjectType.Journal, Authorities.UseBits); if (fromSequence < 1 || toSequence < fromSequence || maximum is < 1 or > 10000) throw Invalid("Invalid journal sequence range or entry limit (1–10000).");
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true); var journal = InspectCore(connection, transaction, name);
        using var command = Command(connection, transaction, """
            SELECT e.journal_id,e.sequence,e.receiver_id,e.version,e.code,e.entry_type,e.captured_at,e.principal,e.job,
                e.object_library,e.object_name,e.member,e.record_number,e.schema_description,e.before_image,e.after_image,e.user_data,r.library,r.name,
                coalesce(length(CAST(e.schema_description AS BLOB)),0)+coalesce(length(CAST(e.before_image AS BLOB)),0)+coalesce(length(CAST(e.after_image AS BLOB)),0)+coalesce(length(e.user_data),0)+256,e.binding_id,e.commit_cycle
            FROM sys_journal_entries e JOIN sys_journal_receivers r ON r.id=e.receiver_id
            WHERE e.journal_id=$journal AND e.sequence>=$from AND ($to IS NULL OR e.sequence<=$to) ORDER BY e.sequence LIMIT $limit
            """);
        command.Parameters.AddWithValue("$journal", journal.Id); command.Parameters.AddWithValue("$from", fromSequence); command.Parameters.AddWithValue("$to", (object?)toSequence ?? DBNull.Value); command.Parameters.AddWithValue("$limit", maximum);
        var rows = new List<JournalEntry>(); var checkedReceivers = new HashSet<QualifiedName>(); long bytes = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint(); var receiver = new QualifiedName(reader.GetString(17), reader.GetString(18));
            var size = reader.GetInt64(19); if (size > 67108864) throw Invalid("Journal entry exceeds the 64 MiB read-page limit.");
            if (bytes + size > 67108864) break; bytes += size;
            if (checkedReceivers.Add(receiver)) Require(receiver, ObjectType.JournalReceiver, Authorities.UseBits);
            string? Optional(int at) => reader.IsDBNull(at) ? null : reader.GetString(at);
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), receiver, reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), Optional(7), Optional(8), Optional(9), Optional(10), Optional(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12), Optional(13), Image(Optional(14)), Image(Optional(15)), reader.IsDBNull(16) ? null : (byte[])reader[16], Optional(20), Optional(21)));
        }
        Require(name, ObjectType.Journal, Authorities.UseBits); foreach (var receiver in checkedReceivers) Require(receiver, ObjectType.JournalReceiver, Authorities.UseBits);
        return rows;
    }
    public IReadOnlyList<JournalReceiverInfo> Receivers(QualifiedName name)
    {
        Require(name, ObjectType.Journal, Authorities.UseBits); using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true); var journal = InspectCore(connection, transaction, name);
        using var command = Command(connection, transaction, "SELECT library,name FROM sys_journal_receivers WHERE journal_id=$journal ORDER BY chain"); command.Parameters.AddWithValue("$journal", journal.Id);
        var names = new List<QualifiedName>(); using (var reader = command.ExecuteReader()) while (reader.Read()) names.Add(new(reader.GetString(0), reader.GetString(1)));
        return names.Select(receiver => { Require(receiver, ObjectType.JournalReceiver, Authorities.UseBits); return ReceiverCore(connection, transaction, receiver); }).ToArray();
    }
    public void DeleteReceiver(QualifiedName name, bool acknowledgeUnsaved = false, CancellationToken cancellationToken = default)
    {
        Require(name, ObjectType.JournalReceiver, AuthorityBit.ObjectExist); new JobLockStore(factory).RequireNoPersistentAllocation(new(name.Library, name.Name.Value, ObjectType.JournalReceiver));
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken); var receiver = ReceiverCore(connection, transaction, name);
        if (receiver.Attached) throw Invalid("Detach the current receiver before deleting it.");
        using (var order = Command(connection, transaction, "SELECT count(*) FROM sys_journal_receivers WHERE origin_id=(SELECT origin_id FROM sys_journal_receivers WHERE id=$id) AND chain<(SELECT chain FROM sys_journal_receivers WHERE id=$id)"))
        { order.Parameters.AddWithValue("$id", receiver.Id); if (Convert.ToInt64(order.ExecuteScalar()) != 0) throw Invalid("Delete receivers from the oldest end of the chain."); }
        if (!acknowledgeUnsaved && !receiver.Saved && receiver.AttachedAt is not null) throw Invalid("Receiver is not saved; specify DLTOPT(*IGNINQMSG) to acknowledge deletion.");
        if (acknowledgeUnsaved) connection.CreateFunction("ipc_journal_force_delete", () => 1);
        ObjectCatalogOperations.DeleteCore(name, ObjectType.JournalReceiver, connection, transaction); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    public void Delete(QualifiedName name, CancellationToken cancellationToken = default)
    {
        Require(name, ObjectType.Journal, AuthorityBit.ObjectExist); new JobLockStore(factory).RequireNoPersistentAllocation(new(name.Library, name.Name.Value, ObjectType.Journal));
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken); var journal = InspectCore(connection, transaction, name);
        if (journal.AttachedFiles != 0) throw Invalid("End file journaling before deleting the journal.");
        ObjectCatalogOperations.DeleteCore(name, ObjectType.Journal, connection, transaction); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    private static JournalInfo InspectCore(SqliteConnection connection, SqliteTransaction? transaction, QualifiedName name)
    {
        using var command = Command(connection, transaction, """
            SELECT j.id,r.library,r.name,j.sequence,(SELECT count(*) FROM sys_journal_bindings b WHERE b.journal_id=j.id)
            FROM sys_journals j JOIN sys_journal_receivers r ON r.id=j.current_receiver WHERE j.library=$library AND j.name=$name
            """); Bind(command, name); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new CpfException("CPF9801", "Journal payload was not found.");
        return new(reader.GetString(0), name, new(reader.GetString(1), reader.GetString(2)), reader.GetInt64(3), reader.GetInt32(4));
    }
    private static JournalReceiverInfo ReceiverCore(SqliteConnection connection, SqliteTransaction? transaction, QualifiedName name)
    {
        using var command = Command(connection, transaction, """
            SELECT r.id,r.journal_id,r.origin_journal,r.attached_at,r.detached_at,r.first_sequence,r.last_sequence,r.bytes,r.threshold_kb,r.saved,
                EXISTS(SELECT 1 FROM sys_journals j WHERE j.current_receiver=r.id)
            FROM sys_journal_receivers r WHERE r.library=$library AND r.name=$name
            """); Bind(command, name); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new CpfException("CPF9801", "Journal receiver payload was not found.");
        string? Text(int at) => reader.IsDBNull(at) ? null : reader.GetString(at);
        DateTimeOffset? Date(int at) => Text(at) is { } value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : null;
        return new(reader.GetString(0), name, Text(1), Text(2), Date(3), Date(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetInt32(9) != 0, reader.GetInt32(10) != 0);
    }
    internal static IReadOnlyList<JournalCell>? Image(string? text)
    {
        if (text is null) return null; using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("Invalid journal image array.");
        return document.RootElement.EnumerateArray().Select(cell =>
        {
            if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != 3 || cell[0].ValueKind != JsonValueKind.String || cell[1].ValueKind != JsonValueKind.String || cell[2].ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid("Invalid journal image cell.");
            var type = cell[1].GetString()!;
            if (type is not ("null" or "integer" or "real" or "text" or "blob") || (type == "null") != (cell[2].ValueKind == JsonValueKind.Null)) throw Invalid("Invalid journal storage type/value.");
            return new JournalCell(cell[0].GetString()!, type, cell[2].ValueKind == JsonValueKind.Null ? null : cell[2].GetString());
        }).ToArray();
    }
}
