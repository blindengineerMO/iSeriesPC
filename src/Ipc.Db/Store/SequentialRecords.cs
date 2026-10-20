using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Work;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record CursorOrder(string Expression, bool Descending);
    private static string CursorFingerprint(FileDefinition definition) => System.Text.Json.JsonSerializer.Serialize(new {
        definition.Attribute,
        Formats = definition.Formats.Select(format => new { format.Name, format.RecordLength, format.BufferLayoutVersion, Fields = format.Fields.Select(f => new {
            f.Name, f.Type, f.Length, f.Decimals, f.DeclaredDigits, f.Position, f.Ccsid, f.VariableLength, f.NullCapable, f.Sequence, f.Descending
        }) }),
        Logical = definition.Logical is { } lf ? new { lf.SourceLibrary, lf.SourceFile, lf.SourceFormat, lf.Rules, lf.DefaultSelect, lf.Members } : null
    });
    public DatabaseRecordCursor OpenSequentialCursor(string library, string name, string member, bool keyed = true, CancellationToken cancellationToken = default)
        => OpenCursorCore(library, name, member, keyed, cancellationToken, false, null);
    public DatabaseRecordCursor OpenUpdateCursor(string library, string name, string member, bool keyed = true, TimeSpan? lockWait = null, CancellationToken cancellationToken = default, bool deleteAccess = false)
        => OpenCursorCore(library, name, member, keyed, cancellationToken, true, lockWait, deleteAccess);
    private DatabaseRecordCursor OpenCursorCore(string library, string name, string member, bool keyed, CancellationToken cancellationToken, bool updateRead, TimeSpan? lockWait, bool deleteAccess = false)
    {
        library = ResolveLibrary(library);
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        var wait = lockWait ?? TimeSpan.Zero;
        if (wait != Timeout.InfiniteTimeSpan && (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(32767)))
            throw LogicalError("Record lock wait must be 0–32767 seconds or an indefinite wait.");
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Formats.Count != 1) throw LogicalError("Sequential cursor requires one record format.");
        if (definition.SqlView is not null)
        {
            if (updateRead) throw LogicalError("SQL view update cursors require the remaining view update implementation.");
            return OpenSqlViewCursor(library, name, definition, cancellationToken);
        }
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
        if (updateRead && owner?.Job is null) throw new CpfException("CPF1241", "Update reads require an executing job.");
        var allocations = new List<IDisposable>(); var locks = new JobLockStore(_factory);
        var updateLock = updateRead ? new CursorUpdateRead(cancellationToken, _factory.ShutdownToken, scope => locks.HasRetainedRead(owner!.Job!.Value, scope)) : null;
        if (updateLock is not null) cancellationToken = updateLock.Token;
        var updateGate = new object();
        void ValidateOwner(bool active = true)
        {
            var caller = Ipc.Services.Events.OperationIdentity.Current;
            if (caller?.Job != owner?.Job || !string.Equals(caller?.Principal, owner?.Principal, StringComparison.OrdinalIgnoreCase))
                throw new CpfException("CPF9802", "Sequential cursor belongs to another execution context.");
            if (updateRead && active) new JobDataAreaStore(_factory).RequireOwner(owner!.Job!.Value);
        }
        void RequireUpdateMode()
        {
            if (!updateRead) return;
            RequireUnenlisted(sourceLibrary, sourceName, "Retained update-read locking");
            var permission = AuthorityBit.Update | (deleteAccess ? AuthorityBit.Delete : AuthorityBit.None);
            Authorize(library, name, permission);
            if (sourceLibrary != library || sourceName != name) Authorize(sourceLibrary, sourceName, permission);
        }
        void Unlock() { ValidateOwner(active: false); updateLock!.Release(); }
        IDisposable? OpenAllocation(JobLockResource resource) => updateRead ? locks.AcquireWithLifetime(owner!.Job!.Value, resource,
            JobLockMode.SharedUpdate, TimeSpan.Zero, cancellationToken, "UpdateOpen", updateLock!.Owner) : locks.AcquireReadScope(resource);
        try
        {
            if (updateLock is not null) allocations.Add(updateLock);
            ValidateOwner(); RequireUpdateMode();
            if (OpenAllocation(new(library, name, ObjectType.File)) is { } allocation) allocations.Add(allocation);
            if ((sourceLibrary != library || sourceName != name) && OpenAllocation(new(sourceLibrary, sourceName, ObjectType.File)) is { } sourceAllocation) allocations.Add(sourceAllocation);
            var original = CursorFingerprint(definition); var originalSource = CursorFingerprint(source);
            DatabaseRecordCursor.Row? Read(DatabaseRecordCursor.Request request)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateOwner(); RequireUpdateMode();
                Authorize(library, name, AuthorityBit.Read);
                if (GetDefinition(library, name) is not { } live || CursorFingerprint(live) != original) throw LogicalError("File definition changed while its cursor was open.");
                if (sourceLibrary != library || sourceName != name)
                {
                    Authorize(sourceLibrary, sourceName, AuthorityBit.Read);
                    if (GetDefinition(sourceLibrary, sourceName) is not { } liveSource || CursorFingerprint(liveSource) != originalSource) throw LogicalError("Physical file changed while its cursor was open.");
                }
                if (request.RecordNumber is not null && members.Length != 1) throw LogicalError("Relative record access requires one bound physical member.");
                if (request.Action == DatabaseRecordCursor.Operation.Validate || members.Length == 0) return null;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = Query(request); if (candidate is null || request.PositionOnly || !candidate.Matches) return candidate;
                    var resource = new JobLockResource(sourceLibrary, sourceName, ObjectType.File, members[candidate.Member], candidate.Number);
                    if (request.LockRecord && updateLock is not null)
                    {
                        IDisposable? retained;
                        try { retained = locks.AcquireWithLifetime(owner!.Job!.Value, resource, JobLockMode.ExclusiveRead, wait,
                            cancellationToken, "UpdateRead", updateLock.Owner, recordOwner: updateLock.Owner); }
                        catch (CpfException error) when (error.MessageId == "CPF5001") { continue; }
                        catch (CpfException error) when (error.MessageId == "CPF1002")
                        { DatabaseRecordErrors.MarkRecordLockFailure(error); request.RecordBlocked?.Invoke(candidate.Position); throw; }
                        try
                        {
                            // A lock wait must not return a stale image or a record
                            // that no longer occupies the requested key position.
                            _ = Read(new(DatabaseRecordCursor.Operation.Validate));
                            var locked = Query(request);
                            if (locked is not null && locked.Matches && locked.Number == candidate.Number && locked.Member == candidate.Member)
                            { updateLock.Hold(retained); retained = null; return locked; }
                        }
                        finally { retained?.Dispose(); }
                        continue;
                    }
                    using var record = locks.AcquireReadScope(resource);
                    // The first query locates a candidate without holding its row lock.
                    // Read again after acquiring it so a concurrent writer cannot make
                    // us return a value observed before it released its exclusive lock.
                    var current = Query(request);
                    if (current is not null && current.Number == candidate.Number && current.Member == candidate.Member) return current;
                }
                throw new CpfException("IPC0138", "Sequential cursor could not stabilize under concurrent changes.");
            }
            DatabaseRecordCursor.Row? Query(DatabaseRecordCursor.Request request)
            {
                using var data = OpenRecordData(sourceLibrary, sourceName, cancellationToken: cancellationToken, members: members);
                var connection = data.Connection; using var command = connection.CreateCommand(); command.Transaction = data.Transaction;
                var predicate = definition.Logical is { } lf ? LogicalPredicate(command, lf, source.PrimaryFormat) : "1";
                var columns = string.Join(',', format.Fields.Select(f => Quote(f.Name)));
                if (request.RecordNumber is { } number) command.Parameters.AddWithValue("$relative", number);
                var schemas = new string[members.Length];
                var branches = members.Select((m, index) => {
                    var table = MemberTable(sourceLibrary, sourceName, m);
                    schemas[index] = CursorTableSchema(connection, data.Transaction, table);
                    var physicalColumns = JournalHooks.Columns(connection, data.Transaction!, table);
                    var rowid = CursorRowid(physicalColumns);
                    var relative = request.RecordNumber is null ? "" : " AND p." + Quote(rowid) +
                        (request.Action == DatabaseRecordCursor.Operation.Lower ? ">=" : request.Action == DatabaseRecordCursor.Operation.Upper ? ">" : "=") + "$relative";
                    var incarnation = data.IsWorkspace ? "NULL" : RecordSlots.VersionExpression(sourceLibrary, sourceName, m, "p." + Quote(rowid));
                    return $"SELECT {columns},p.{Quote(rowid)} AS __ipc_record_number__,{index} AS __ipc_member_order__,{JournalHooks.Image("p", physicalColumns)} AS __ipc_before_image__,{incarnation} AS __ipc_version__ FROM {Quote(table)} p WHERE ({predicate}){relative}";
                });
                var values = order.Select((term, index) => term.Expression + " AS __ipc_seek_" + index + "__");
                var query = "SELECT * FROM (SELECT b.*," + string.Join(',', values) + " FROM (" + string.Join(" UNION ALL ", branches) + ") AS b)";
                var conditions = new List<string>(); var match = "1";
                if (request.RecordNumber is not null) match = "__ipc_record_number__=$relative";
                if (request.Position is { } position)
                {
                    if (request.Action == DatabaseRecordCursor.Operation.Refresh)
                    {
                        command.Parameters.AddWithValue("$member", position[^2]!); command.Parameters.AddWithValue("$rrn", position[^1]!);
                        conditions.Add("__ipc_member_order__=$member AND __ipc_record_number__=$rrn");
                    }
                    else
                    {
                        var terms = order.Select((term, index) => {
                            var parameter = "$seek" + index; command.Parameters.AddWithValue(parameter, position[index] ?? DBNull.Value);
                            return (Column: "__ipc_seek_" + index + "__", Value: parameter, term.Descending);
                        }).ToArray();
                        conditions.Add(CursorComparison(terms, request.Reverse, request.Inclusive));
                    }
                }
                if (request.Key is not null || request.EqualPosition is not null)
                {
                    var terms = CursorSearchTerms(command, format, keyed, request.Key, request.EqualPosition);
                    match = string.Join(" AND ", terms.Select(term => term.Column + " IS " + term.Value));
                    if (request.Action == DatabaseRecordCursor.Operation.Chain) conditions.Add(match);
                    else if (request.PositionOnly) conditions.Add(CursorComparison(terms, false, request.Action == DatabaseRecordCursor.Operation.Lower));
                }
                var projected = request.PositionOnly ? "__ipc_record_number__,__ipc_member_order__," + string.Join(',', order.Select((_, index) => "__ipc_seek_" + index + "__")) : "*";
                query = "SELECT " + projected + ",CASE WHEN " + match + " THEN 1 ELSE 0 END FROM (" + query + ")";
                if (conditions.Count != 0) query += " WHERE " + string.Join(" AND ", conditions.Select(condition => "(" + condition + ")"));
                var sort = request.RecordNumber is not null ? "__ipc_record_number__" :
                    string.Join(',', order.Select((term, index) => "__ipc_seek_" + index + "__" + (term.Descending != request.Reverse ? " DESC" : "")));
                command.CommandText = query + " ORDER BY " + sort + " LIMIT 1";
                try
                {
                    data.Prepare(command);
                    return Ipc.Services.Sqlite.SqliteCancellation.Run<DatabaseRecordCursor.Row?>(connection, cancellationToken, () => {
                    using var reader = command.ExecuteReader();
                    if (!reader.Read()) return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = request.PositionOnly ? 0 : format.Fields.Count;
                    var keyOffset = offset + (request.PositionOnly ? 2 : 4);
                    var matches = reader.GetBoolean(keyOffset + order.Count);
                    var valuesRead = request.PositionOnly || !matches ? new Dictionary<string, object?>() : ReadRow(reader, format);
                    var next = Enumerable.Range(keyOffset, order.Count).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
                    var sourceMember = reader.GetInt32(offset + 1);
                    return new(valuesRead, reader.GetInt64(offset), sourceMember, next, matches,
                        request.PositionOnly || !matches ? null : reader.GetString(offset + 2), schemas[sourceMember], request.PositionOnly || !matches || reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3));
                    });
                }
                catch (SqliteException error) when (error.SqliteErrorCode == 1) { throw LogicalError("Invalid stored file value or sequential query definition."); }
            }
            JobRecordTransactions.CursorRegistration? registration = null;
            DatabaseRecordCursor? cursor = null; var registrationGate = new object();
            void Register()
            {
                if (updateRead || registration is not null || owner?.Job is not { } job) return;
                var opened = cursor ?? throw new InvalidOperationException("Cursor is not initialized.");
                registration = _factory.RecordTransactions.RegisterCursor(job, [new(sourceLibrary, sourceName)],
                    opened.CapturePosition, opened.RestorePosition, cancellationToken);
                if (registration is not null) allocations.Add(registration);
            }
            IDisposable? EnterCursor(bool closing)
            {
                if (updateRead) { Monitor.Enter(updateGate); return new CursorUpdateGate(updateGate); }
                lock (registrationGate)
                {
                    if (!closing) Register();
                    return registration?.Enter(closing, cancellationToken);
                }
            }
            void Mutate(DatabaseRecordCursor.Row row, IReadOnlyDictionary<string, object?>? changes)
            {
                _ = Read(new(DatabaseRecordCursor.Operation.Validate));
                MutateCursorRecord(library, name, definition, sourceLibrary, sourceName, source,
                    members, row, changes, cancellationToken, updateLock?.Owner, updateRead ? wait : TimeSpan.Zero);
            }
            void RequireDelete()
            {
                _ = Read(new(DatabaseRecordCursor.Operation.Validate));
                Authorize(library, name, AuthorityBit.Delete);
            }
            void Rebind(CancellationToken replacement)
            {
                replacement.ThrowIfCancellationRequested();
                var caller = Ipc.Services.Events.OperationIdentity.Current;
                if (caller?.Job != owner?.Job || !string.Equals(caller?.Principal, owner?.Principal, StringComparison.OrdinalIgnoreCase))
                    throw new CpfException("CPF9802", "Sequential cursor belongs to another execution context.");
                lock (registrationGate)
                {
                    using var updateOperation = updateRead ? EnterCursor(false) : null;
                    using var operation = registration?.Enter(cancellationToken: replacement);
                    var previous = cancellationToken;
                    if (updateLock is not null)
                    {
                        cancellationToken = updateLock.Rebind(replacement);
                        _ = Read(new(DatabaseRecordCursor.Operation.Validate));
                    }
                    else
                    {
                        cancellationToken = replacement;
                        try { _ = Read(new(DatabaseRecordCursor.Operation.Validate)); }
                        catch { cancellationToken = previous; throw; }
                    }
                }
            }
            cursor = new DatabaseRecordCursor(Read, allocations, EnterCursor, Mutate, RequireDelete, Rebind,
                updateRead ? Unlock : null, updateRead ? () => updateLock!.Held : null);
            Register();
            if (updateRead) allocations.Add(_factory.RegisterRuntimeResource(cursor));
            return cursor;
        }
        catch { foreach (var allocation in allocations.AsEnumerable().Reverse()) allocation.Dispose(); throw; }
    }
    private static string CursorComparison(IReadOnlyList<(string Column, string Value, bool Descending)> terms, bool reverse, bool inclusive)
    {
        var prefixes = new List<string>(); var alternatives = new List<string>();
        foreach (var term in terms)
        {
            alternatives.Add("(" + string.Join(" AND ", prefixes.Append(term.Column + (term.Descending != reverse ? " < " : " > ") + term.Value)) + ")");
            prefixes.Add(term.Column + " IS " + term.Value);
        }
        if (inclusive) alternatives.Add("(" + string.Join(" AND ", prefixes) + ")");
        return "(" + string.Join(" OR ", alternatives) + ")";
    }
    private static IReadOnlyList<(string Column, string Value, bool Descending)> CursorSearchTerms(SqliteCommand command, RecordFormat format, bool keyed,
        IReadOnlyDictionary<string, object?>? key, object?[]? position)
    {
        if (key is not null)
        {
            try { key = key.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase); }
            catch (ArgumentException) { throw LogicalError("Duplicate key field names."); }
        }
        var fields = keyed ? format.Fields.Where(field => field.Sequence > 0).OrderBy(field => field.Sequence).ToArray() : [];
        if (fields.Length == 0) throw LogicalError("A keyed cursor operation requires declared key fields.");
        var result = new List<(string Column, string Value, bool Descending)>(); var index = 0; var used = 0;
        foreach (var field in fields)
        {
            if (key is not null && !key.TryGetValue(field.Name, out _)) break;
            if (key is not null)
            {
                var value = key[field.Name]; if (value is null && !field.NullCapable) throw LogicalError("Null key requires a null-capable field.");
                var parameter = "$key" + used; command.Parameters.AddWithValue(parameter, RecordSqlValue(field, value) ?? DBNull.Value);
                var expression = LogicalExpression(field, parameter);
                if (field.NullCapable) result.Add(("__ipc_seek_" + index++ + "__", "(" + expression + " IS NULL)", field.Descending));
                result.Add(("__ipc_seek_" + index++ + "__", expression, field.Descending));
            }
            else
            {
                if (field.NullCapable) { var parameter = "$key" + index; command.Parameters.AddWithValue(parameter, position![index] ?? DBNull.Value); result.Add(("__ipc_seek_" + index++ + "__", parameter, field.Descending)); }
                var valueParameter = "$key" + index; command.Parameters.AddWithValue(valueParameter, position![index] ?? DBNull.Value); result.Add(("__ipc_seek_" + index++ + "__", valueParameter, field.Descending));
            }
            used++;
        }
        if (used == 0 || key is not null && used != key.Count) throw LogicalError("Search requires a contiguous leftmost key prefix.");
        return result;
    }

}
