using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record SqlChangedRow(long Number, object?[] Values);

    private static (SqlBound? Predicate, List<(FieldSpec Field, SqlBound Value)> Assignments) BindSqlMutation(FileDefinition definition, SqlStatement statement, SqlExecutionOptions options, string name,
        Func<SqlColumn, SqlBound>? resolveColumn = null, Func<string, FieldSpec>? resolveTarget = null, FileDefinition? defaults = null, DateTimeOffset? statementTime = null)
    {
        var update = statement as SqlUpdate; var delete = statement as SqlDelete;
        var format = definition.PrimaryFormat; var now = statementTime ?? DateTimeOffset.UtcNow;
        var binder = new SqlBinder(format, update?.Alias ?? delete?.Alias ?? name, options.Ccsid, now, resolveColumn, conversion: options.Conversion);
        var predicate = (update?.Where ?? delete?.Where) is { } expression ? binder.Bind(expression) : null;
        if (predicate is not null) SqlBinder.Predicate(predicate);
        var assignments = new List<(FieldSpec Field, SqlBound Value)>();
        if (update is not null) foreach (var assignment in update.Assignments)
        {
            var field = format.Find(assignment.Column) ?? throw new CpfException("SQL0206", "Unknown UPDATE column " + assignment.Column + ".");
            field = resolveTarget?.Invoke(assignment.Column) ?? field;
            if (assignments.Any(item => item.Field.Name == field.Name)) throw new CpfException("SQL0612", "A target column is assigned more than once.");
            var value = assignment.Value;
            if (value is SqlConstant { Value.Kind: SqlValueKind.Default })
            {
                if ((defaults ?? definition).SqlTable is not null && !field.NullCapable && field.DefaultValue is null && !field.CurrentDatetimeDefault)
                    throw new CpfException("SQL0407", "Column " + field.Name + " has no usable default.");
                var raw = field.CurrentDatetimeDefault ? SqlNow(field.Type, now) : DefaultFor(field);
                assignments.Add((field, new(SqlKind.Null, _ => raw))); // Defaults already have the destination type.
            }
            else
            {
                var bound = binder.Bind(value);
                if (bound.Kind == SqlKind.Boolean && field.Type != FieldType.Logic) throw new CpfException("SQL0104", "Predicate cannot be assigned to this column.");
                if (bound.Kind != SqlKind.Null && !(bound.Kind == SqlKind.Text && bound.Literal))
                {
                    var compatible = field.IsNumeric ? bound.Numeric : field.Type switch {
                        FieldType.Alpha => bound.Kind == SqlKind.Text, FieldType.Logic => bound.Kind == SqlKind.Boolean,
                        FieldType.Date => bound.Kind == SqlKind.Date, FieldType.Time => bound.Kind == SqlKind.Time,
                        FieldType.Timestamp => bound.Kind == SqlKind.Timestamp, _ => false };
                    if (!compatible) throw new CpfException("SQL0408", "Incompatible assignment type for column " + field.Name + ".");
                }
                assignments.Add((field, bound));
            }
        }
        return (predicate, assignments);
    }

    private long MutateSqlRows(string library, string name, SqlStatement statement, SqlExecutionOptions options, CancellationToken token)
    {
        var update = statement as SqlUpdate; var delete = statement as SqlDelete;
        var authority = update is null ? AuthorityBit.Delete : AuthorityBit.Update;
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        IDisposable? selectedLocks = null; ForeignCascadeLocks? cascades = null; IDisposable? foreignDdl = null; string? foreignTarget = null; IReadOnlyList<RestrictedForeignUpdate> restrictions = [];
        try
        {
            if (OperationIdentity.Current?.Job is { } owner) new JobDataAreaStore(_factory).RequireOwner(owner);
            var root = new QualifiedName(library, name); var now = DateTimeOffset.UtcNow;
            var requested = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("SQL0204", "Table not found.");
            SqlViewWritePlan? view = requested.SqlView is null ? null : BindViewWritePlan(connection, transaction, root, requested, now, token);
            var definition = view?.Definition ?? requested;
            if (definition.Logical is not null || definition.Formats.Count != 1 || definition.Attribute != Definitions.FileAttribute.Physical)
                throw new CpfException("SQL0104", "SQL UPDATE/DELETE requires a physical table or deletable view.");
            if (update is not null && view is { Writable.Count: 0 }) throw new CpfException("SQL0150", "View has no updatable columns.");
            var format = definition.PrimaryFormat; var qualifier = update?.Alias ?? delete?.Alias ?? name;
            var (predicate, assignments) = BindSqlMutation(requested, statement, options, name,
                view is null ? null : column => ResolveViewColumn(view, column, qualifier),
                view is null ? null : column => view.Writable.TryGetValue(column, out var field) ? field : throw new CpfException("SQL0151", "View column is not updatable."),
                definition, now);
            if (predicate?.Reads == true || assignments.Any(item => item.Value.Reads)) authority |= AuthorityBit.Read;
            var callerAuthority = authority; var operation = update is null ? AuthorityBit.Delete : AuthorityBit.Update;
            void ValidateRoot()
            {
                if (view is not null) AuthorizeViewWrite(connection,transaction,view,callerAuthority,operation,acquireLocks:false);
                else new ServiceAuthorization(_factory).RequireBoundObject(connection,transaction,root.Library,root.Name.Value,AuthorityBit.ObjectOperate | callerAuthority);
            }
            if (view is null) { Authorize(library, name, authority); RequireUnenlisted(library, name, "SQL COMMIT(*NONE)"); }
            else
            {
                AuthorizeViewWrite(connection, transaction, view, callerAuthority, operation);
                library = view.Physical.Library; name = view.Physical.Name.Value;
            }
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
            command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
            var member = command.ExecuteScalar() as string ?? throw new CpfException("SQL0204", "Table has no member.");
            var table = MemberTable(library, name, member); foreignTarget = table; var rowid = RowIdentitySql(format);
            EnsureExactDecimalStorage(connection, transaction, table, format);
            command.Parameters.Clear();
            command.CommandText = "SELECT " + Quote(rowid) + "," + string.Join(',', format.Fields.Select(field => Quote(field.Name))) + " FROM " + Quote(table) + " ORDER BY " + Quote(rowid);
            var rows = new List<SqlChangedRow>(); long bytes = 0; var cells = Math.Max(1, assignments.Count + 1);
            SqliteCancellation.Run(connection, token, () => {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                    var original = new object?[format.Fields.Count];
                    for (var index = 0; index < original.Length; index++) original[index] = reader.IsDBNull(index + 1) ? null : reader.GetValue(index + 1);
                    if (view is not null && view.Filters.Any(filter => filter.Predicate.Evaluate(original) is not true)) continue;
                    if (predicate is not null && predicate.Evaluate(original) is not true) continue;
                    if ((long)(rows.Count + 1) * cells > 1000000) throw new CpfException("SQL0904", "SQL mutation exceeds one million staged cells.");
                    var values = new object?[assignments.Count]; bytes += 16;
                    for (var index = 0; index < values.Length; index++)
                    {
                        var item = assignments[index]; var raw = item.Value.Evaluate(original);
                        if (raw is SqlExactNumber exact)
                            raw = item.Field.IsNumeric ? exact.Rescale(item.Field.Type is FieldType.Packed or FieldType.Zoned ? item.Field.Decimals : item.Field.Type == FieldType.Binary ? 0 : exact.Scale).ToString() : exact.ToString();
                        else if (raw is double floating && item.Field.Type is FieldType.Binary or FieldType.Packed or FieldType.Zoned)
                            raw = SqlExactNumber.Parse(floating.ToString("R", CultureInfo.InvariantCulture)).Rescale(item.Field.Type == FieldType.Binary ? 0 : item.Field.Decimals).ToString();
                        values[index] = SqlFieldValue(item.Field, raw, options.Conversion);
                        bytes += values[index] is string text ? Encoding.UTF8.GetByteCount(text) : 16;
                    }
                    if (view is { Checks.Count: > 0 } && update is not null)
                    {
                        var changed = (object?[])original.Clone();
                        for (var index = 0; index < assignments.Count; index++) changed[format.Fields.FindIndex(field => field.Name == assignments[index].Field.Name)] = values[index];
                        CheckViewRow(view, changed);
                    }
                    if (bytes > 67108864) throw new CpfException("SQL0904", "SQL mutation exceeds 64 MiB of staged values.");
                    rows.Add(new(reader.GetInt64(0), values));
                }
                return 0;
            });
            if (OperationIdentity.Current?.Job is { } job)
                selectedLocks = new JobLockStore(_factory).AcquireStatementRows(job, new(library, name, ObjectType.File, member), rows.Select(row => row.Number).ToHashSet(), authority, token,
                    view is null ? null : () => AuthorizeViewWrite(connection, transaction, view, callerAuthority, operation, acquireLocks: false));
            var selectedRows = rows.Select(row => row.Number).ToHashSet();
            cascades = update is null ? LockForeignCascades(connection,transaction,new(library,name),member,selectedRows,token,ValidateRoot) :
                LockForeignParentChecks(connection,transaction,new(library,name),member,selectedRows,assignments.Select(item => item.Field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),token,ValidateRoot);
            var referenced = false;
            var indexes = update is not null && rows.Count != 0 ? DeferSqlUniqueIndexes(connection, transaction, table, assignments.Select(item => item.Field.Name).ToHashSet(), token, ref referenced) : [];
            if (referenced) { foreignDdl = new ForeignDdlScope(connection); restrictions = BindRestrictedForeignUpdates(connection,transaction,table,rowid,assignments); }
            command.CommandText = update is null ? "DELETE FROM " + Quote(table) + " WHERE " + Quote(rowid) + "=$rrn" :
                "UPDATE " + Quote(table) + " SET " + string.Join(',', assignments.Select((item, index) => Quote(item.Field.Name) + "=$v" + index)) + " WHERE " + Quote(rowid) + "=$rrn";
            command.Parameters.AddWithValue("$rrn", 0L);
            for (var index = 0; index < assignments.Count; index++) command.Parameters.AddWithValue("$v" + index, DBNull.Value);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
                if (referenced) CheckRestrictedForeignUpdate(restrictions,row.Number,row.Values);
                command.Parameters["$rrn"].Value = row.Number;
                for (var index = 0; index < row.Values.Length; index++) command.Parameters["$v" + index].Value = row.Values[index] ?? DBNull.Value;
                SqliteCancellation.Run(connection, token, command.ExecuteNonQuery);
            }
            foreach (var sql in indexes) { command.CommandText = sql; SqliteCancellation.Run(connection, token, command.ExecuteNonQuery); }
            foreignDdl?.Dispose(); foreignDdl = null;
            token.ThrowIfCancellationRequested();
            ValidateRoot();
            // Transactional index DDL owns the shared-cache schema lock. Validate
            // final credentials/job state on this publisher's existing connection,
            // as record-unit publication does, rather than opening a second one.
            if (OperationIdentity.Current is { } identity)
                new ServiceAuthorization(_factory).RequireCaller(connection, transaction, identity.Principal);
            if (OperationIdentity.Current?.Job is { } live)
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$job AND name=$name AND user=$user AND execution_state='Running'";
                command.Parameters.AddWithValue("$job", live.Number); command.Parameters.AddWithValue("$name", live.Name); command.Parameters.AddWithValue("$user", live.User);
                var state = command.ExecuteScalar();
                if (state is null) throw new CpfException("CPF1241", "SQL owner is no longer an active job.");
                if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("Job cancellation was requested.");
            }
            cascades?.Validate();
            if (referenced && ForeignMutationError(connection,transaction,statement,table) is { } violation) throw violation;
            token.ThrowIfCancellationRequested(); transaction.Commit(); return rows.Count;
        }
        catch (SqliteException error) when (IsForeignFailure(error))
        { throw ForeignMutationError(connection,transaction,statement,foreignTarget,failed:true)!; }
        finally
        {
            foreach (var restriction in restrictions) restriction.Command.Dispose();
            foreignDdl?.Dispose();
            // Publication/rollback must finish before another record owner can proceed.
            try { transaction.Dispose(); } finally { try { cascades?.Dispose(); } finally { selectedLocks?.Dispose(); } }
        }
    }

    private static List<string> DeferSqlUniqueIndexes(SqliteConnection connection, SqliteTransaction transaction, string table, HashSet<string> assigned, CancellationToken token, ref bool referenced)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT s.name,s.sql FROM sqlite_schema s JOIN pragma_index_list($table) p ON s.name=p.name WHERE p.\"unique\"=1 AND s.tbl_name=$table";
        command.Parameters.AddWithValue("$table", table);
        var indexes = new List<(string Name, string Sql)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var sql = reader.IsDBNull(1) ? null : reader.GetString(1);
            // Managed physical-file indexes quote every field; defer only indexes
            // whose key is actually assigned, retaining other constraints normally.
            if (sql is null) throw new CpfException("SQL0104", "Implicit unique indexes are not supported by searched UPDATE.");
            if (assigned.Any(column => sql.Contains(Quote(column), StringComparison.Ordinal))) indexes.Add((reader.GetString(0), sql));
        }
        if (indexes.Count == 0) return [];
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var tables = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (var candidate in tables)
        {
            token.ThrowIfCancellationRequested(); command.CommandText = "SELECT count(*) FROM pragma_foreign_key_list($other) WHERE \"table\"=$table COLLATE NOCASE";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$other", candidate); command.Parameters.AddWithValue("$table", table);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                if (SqlForeignCatalog.HasUnregisteredRelations(connection,transaction,table)) throw new CpfException("SQL0104", "Searched UPDATE of a referenced unique key requires native referential-constraint support.");
                referenced = true;
            }
        }
        command.Parameters.Clear();
        foreach (var index in indexes) { command.CommandText = "DROP INDEX " + Quote(index.Name); SqliteCancellation.Run(connection, token, command.ExecuteNonQuery); }
        return indexes.Select(index => index.Sql).ToList();
    }
}
