using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record CursorOrder(string Expression, bool Descending);
    private static string CursorFingerprint(FileDefinition definition) => System.Text.Json.JsonSerializer.Serialize(new {
        definition.Attribute,
        Formats = definition.Formats.Select(format => new { format.BufferLayoutVersion, Fields = format.Fields.Select(f => new {
            f.Name, f.Type, f.Length, f.Decimals, f.DeclaredDigits, f.Position, f.Ccsid, f.VariableLength, f.NullCapable, f.Sequence, f.Descending
        }) }),
        Logical = definition.Logical is { } lf ? new { lf.SourceLibrary, lf.SourceFile, lf.SourceFormat, lf.Rules, lf.DefaultSelect, lf.Members } : null
    });
    public DatabaseRecordCursor OpenSequentialCursor(string library, string name, string member, bool keyed = true, CancellationToken cancellationToken = default)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Formats.Count != 1) throw LogicalError("Sequential cursor requires one record format.");
        var format = definition.PrimaryFormat;
        var sourceLibrary = definition.Logical?.SourceLibrary ?? library;
        var sourceName = definition.Logical?.SourceFile ?? name;
        var source = definition.Logical is null ? definition : GetDefinition(sourceLibrary, sourceName) ?? throw LogicalError("Missing physical file.");
        var members = definition.Logical is { } logical ? logical.Members.TryGetValue(member, out var bound) ? bound.ToArray() : throw LogicalError("Missing logical member binding.") : new[] { member };
        if (definition.Logical is { } view)
        {
            ValidateLogical(view);
            Authorize(sourceLibrary, sourceName, AuthorityBit.Read);
            if (source.Logical is not null || source.Formats.Count != 1 || source.PrimaryFormat.Name != view.SourceFormat ||
                format.Fields.Any(f => source.PrimaryFormat.Find(f.Name) is not { } actual || actual.Type != f.Type || actual.Length != f.Length || actual.Decimals != f.Decimals || actual.Ccsid != f.Ccsid || actual.NullCapable != f.NullCapable || actual.VariableLength != f.VariableLength))
                throw LogicalError("Physical format changed; recompile the logical file.");
        }
        foreach (var physicalMember in members) EnsureMember(sourceLibrary, sourceName, physicalMember);
        var order = new List<CursorOrder>();
        foreach (var key in keyed ? format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence) : Enumerable.Empty<FieldSpec>())
        {
            var expression = LogicalColumn(key);
            if (key.NullCapable) order.Add(new("(" + expression + " IS NULL)", key.Descending));
            order.Add(new(expression, key.Descending));
        }
        order.Add(new("__ipc_member_order__", false)); order.Add(new("__ipc_record_number__", false));
        if (order.Count > 128) throw LogicalError("Sequential cursor sort position exceeds 128 terms.");
        var owner = Ipc.Services.Events.OperationIdentity.Current;
        var allocations = new List<IDisposable>(); var locks = new JobLockStore(_factory);
        try
        {
            if (locks.AcquireReadScope(new(library, name, ObjectType.File)) is { } allocation) allocations.Add(allocation);
            if ((sourceLibrary != library || sourceName != name) && locks.AcquireReadScope(new(sourceLibrary, sourceName, ObjectType.File)) is { } sourceAllocation) allocations.Add(sourceAllocation);
            var original = CursorFingerprint(definition); var originalSource = CursorFingerprint(source);
            DatabaseRecordCursor.Row? Read(object?[]? position)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var caller = Ipc.Services.Events.OperationIdentity.Current;
                if (caller?.Job != owner?.Job || !string.Equals(caller?.Principal, owner?.Principal, StringComparison.OrdinalIgnoreCase))
                    throw new CpfException("CPF9802", "Sequential cursor belongs to another execution context.");
                Authorize(library, name, AuthorityBit.Read);
                if (GetDefinition(library, name) is not { } live || CursorFingerprint(live) != original) throw LogicalError("File definition changed while its cursor was open.");
                if (sourceLibrary != library || sourceName != name)
                {
                    Authorize(sourceLibrary, sourceName, AuthorityBit.Read);
                    if (GetDefinition(sourceLibrary, sourceName) is not { } liveSource || CursorFingerprint(liveSource) != originalSource) throw LogicalError("Physical file changed while its cursor was open.");
                }
                if (members.Length == 0) return null;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = Query(position); if (candidate is null) return null;
                    using var record = locks.AcquireReadScope(new(sourceLibrary, sourceName, ObjectType.File, members[candidate.Member], candidate.Number));
                    // The first query locates a candidate without holding its row lock.
                    // Read again after acquiring it so a concurrent writer cannot make
                    // us return a value observed before it released its exclusive lock.
                    var current = Query(position);
                    if (current is not null && current.Number == candidate.Number && current.Member == candidate.Member) return current;
                }
                throw new CpfException("IPC0138", "Sequential cursor could not stabilize under concurrent changes.");
            }
            DatabaseRecordCursor.Row? Query(object?[]? position)
            {
                using var connection = _factory.Open(); using var command = connection.CreateCommand();
                var predicate = definition.Logical is { } lf ? LogicalPredicate(command, lf, source.PrimaryFormat) : "1";
                var columns = string.Join(',', format.Fields.Select(f => Quote(f.Name)));
                var branches = members.Select((m, index) => $"SELECT {columns},{RowIdentitySql(source.PrimaryFormat)} AS __ipc_record_number__,{index} AS __ipc_member_order__ FROM {Quote(MemberTable(sourceLibrary, sourceName, m))} WHERE {predicate}");
                var values = order.Select((term, index) => term.Expression + " AS __ipc_seek_" + index + "__");
                var query = "SELECT * FROM (SELECT b.*," + string.Join(',', values) + " FROM (" + string.Join(" UNION ALL ", branches) + ") AS b)";
                if (position is not null)
                {
                    var prefixes = new List<string>(); var alternatives = new List<string>();
                    for (var index = 0; index < order.Count; index++)
                    {
                        var column = "__ipc_seek_" + index + "__"; var parameter = "$seek" + index;
                        command.Parameters.AddWithValue(parameter, position[index] ?? DBNull.Value);
                        alternatives.Add("(" + string.Join(" AND ", prefixes.Append(column + (order[index].Descending ? " < " : " > ") + parameter)) + ")");
                        prefixes.Add(column + " IS " + parameter);
                    }
                    query += " WHERE " + string.Join(" OR ", alternatives);
                }
                command.CommandText = query + " ORDER BY " + string.Join(',', order.Select((term, index) => "__ipc_seek_" + index + "__" + (term.Descending ? " DESC" : ""))) + " LIMIT 1";
                try
                {
                    return Ipc.Services.Sqlite.SqliteCancellation.Run<DatabaseRecordCursor.Row?>(connection, cancellationToken, () => {
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var valuesRead = ReadRow(reader, format);
                    var next = Enumerable.Range(format.Fields.Count + 2, order.Count).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
                    return new(valuesRead, reader.GetInt64(format.Fields.Count), reader.GetInt32(format.Fields.Count + 1), next);
                    });
                }
                catch (SqliteException error) when (error.SqliteErrorCode == 1) { throw LogicalError("Invalid stored file value or sequential query definition."); }
            }
            return new(Read, allocations);
        }
        catch { foreach (var allocation in allocations.AsEnumerable().Reverse()) allocation.Dispose(); throw; }
    }
}
