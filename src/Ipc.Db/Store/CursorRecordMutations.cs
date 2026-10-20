using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static string CursorTableSchema(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table"; command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() as string ?? throw LogicalError("Physical cursor member is unavailable.");
    }
    private static string CursorRowid(IReadOnlyList<JournalColumn> columns) =>
        new[] { "_rowid_", "rowid", "oid" }.FirstOrDefault(alias => columns.All(column => !column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
        ?? throw LogicalError("Member has no available physical record number.");
    private void MutateCursorRecord(string library, string name, FileDefinition definition, string sourceLibrary, string sourceName,
        FileDefinition physicalDefinition, IReadOnlyList<string> members, DatabaseRecordCursor.Row row,
        IReadOnlyDictionary<string, object?>? changes, CancellationToken cancellationToken, string? recordOwner = null, TimeSpan recordWait = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permission = changes is null ? AuthorityBit.Delete : AuthorityBit.Update;
        Authorize(library, name, permission);
        if (sourceLibrary != library || sourceName != name) Authorize(sourceLibrary, sourceName, permission);
        if (row.Number < 1 || row.Member < 0 || row.Member >= members.Count || row.Image is null || row.Schema is null)
            throw LogicalError("Cursor has no valid physical mutation target.");
        var member = members[row.Member]; var format = definition.PrimaryFormat;
        Dictionary<string, object?>? values = null;
        if (changes is not null)
        {
            try { values = changes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase); }
            catch (ArgumentException) { throw LogicalError("Duplicate update field names."); }
            if (values.Count == 0 || values.Keys.Any(field => format.Find(field) is null)) throw LogicalError("Update requires known projection fields.");
        }
        var locks = new JobLockStore(_factory);
        IDisposable? Allocate()
        {
            if (OperationIdentity.Current?.Job is not { } job) return null;
            try { return locks.AcquireWithLifetime(job,
                new(sourceLibrary, sourceName, ObjectType.File, member, row.Number), JobLockMode.Exclusive, recordWait, cancellationToken,
                "RecordMutation", Guid.NewGuid().ToString("N"), recordOwner); }
            catch (CpfException failure) when (failure.MessageId == "CPF5001")
            { throw new CpfException("CPF5026", "The current record no longer exists; read it again before writing."); }
            catch (CpfException failure) when (failure.MessageId == "CPF1002")
            { DatabaseRecordErrors.MarkRecordLockFailure(failure); throw; }
        }
        using var allocation = Allocate();
        using var data = OpenRecordData(sourceLibrary, sourceName, write: true, cancellationToken: cancellationToken, members: [member]);
        var connection = data.Connection; var transaction = data.Transaction!; var table = MemberTable(sourceLibrary, sourceName, member);
        // Recheck authority and model after acquiring the writer; a wait must not
        // publish under permissions or a logical projection that has since changed.
        Authorize(library, name, permission);
        if (sourceLibrary != library || sourceName != name) Authorize(sourceLibrary, sourceName, permission);
        var liveView = GetDefinition(library, name);
        var livePhysical = sourceLibrary == library && sourceName == name ? liveView : GetDefinition(sourceLibrary, sourceName);
        if (liveView is null || livePhysical is null || CursorFingerprint(liveView) != CursorFingerprint(definition) ||
            CursorFingerprint(livePhysical) != CursorFingerprint(physicalDefinition)) throw LogicalError("File definition changed since the record was selected.");
        if (!string.Equals(row.Schema, CursorTableSchema(connection, transaction, table), StringComparison.Ordinal)) throw LogicalError("Physical member schema changed since the record was read.");
        var columns = JournalHooks.Columns(connection, transaction, table); var id = Quote(CursorRowid(columns));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$rrn", row.Number);
        var predicate = definition.Logical is { } logical ? LogicalPredicate(command, logical, physicalDefinition.PrimaryFormat) : "1";
        var where = "p." + id + "=$rrn AND (" + predicate + ")";
        command.CommandText = "SELECT " + JournalHooks.Image("p", columns) + " FROM " + Quote(table) + " p WHERE " + where;
        data.Prepare(command);
        var before = SqliteCancellation.Run(connection, cancellationToken, command.ExecuteScalar) as string;
        if (!string.Equals(row.Image, before, StringComparison.Ordinal)) throw new CpfException("CPF5026", "The current record changed or is no longer selected; read it again before writing.");
        if (!data.IsWorkspace && row.Version is not null)
        {
            command.CommandText = "SELECT " + RecordSlots.VersionExpression(sourceLibrary, sourceName, member, "$rrn");
            if (!string.Equals(command.ExecuteScalar() as string, row.Version, StringComparison.Ordinal))
                throw new CpfException("CPF5026", "The selected record position has been reused or changed; read it again before writing.");
        }
        var sets = new List<string>();
        if (values is not null)
        {
            foreach (var field in format.Fields.Where(field => values.ContainsKey(field.Name)))
            {
                var raw = values[field.Name]; if (raw is null && !field.NullCapable) throw LogicalError("Null is not allowed for field " + field.Name + ".");
                var parameter = "$value" + sets.Count;
                command.Parameters.AddWithValue(parameter, RecordSqlValue(field, raw) ?? DBNull.Value);
                sets.Add(Quote(field.Name) + "=" + parameter);
            }
        }
        if (!data.IsWorkspace)
            data.AddCascadeLocks(values is null ? LockForeignCascades(connection,transaction,new(sourceLibrary,sourceName),member,new HashSet<long> { row.Number },cancellationToken) :
                LockForeignParentChecks(connection,transaction,new(sourceLibrary,sourceName),member,new HashSet<long> { row.Number },values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),cancellationToken));
        command.CommandText = values is null ? "DELETE FROM " + Quote(table) + " AS p WHERE " + where :
            "UPDATE " + Quote(table) + " AS p SET " + string.Join(',', sets) + " WHERE " + where;
        data.Prepare(command);
        if (SqliteCancellation.Run(connection, cancellationToken, command.ExecuteNonQuery) != 1)
            throw new CpfException("CPF5026", "The current-record mutation did not affect exactly one record.");
        data.Complete();
    }
}
