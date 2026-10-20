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
    public long ExecuteSql(string sql, SqlExecutionOptions options, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken);
        cancellationToken = lifetime.Token; cancellationToken.ThrowIfCancellationRequested();
        var statement = SqlStatementParser.Parse(sql, options);
        if (statement is SqlSelect) throw new CpfException("SQL0104", "SELECT requires the query interface.");
        var resource = new SqlWriteResource(lifetime, Environment.CurrentManagedThreadId); IDisposable? registration = null;
        try
        {
            registration = OperationIdentity.Current?.Job is { } job ? _factory.RegisterJobRuntimeResource(job, resource) : _factory.RegisterRuntimeResource(resource);
            return ExecuteSqlStatement(statement, sql, options, cancellationToken);
        }
        finally { resource.Complete(); registration?.Dispose(); }
    }
    private sealed class SqlWriteResource(CancellationTokenSource stop, int thread) : IDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete() => _completed.TrySetResult();
        public void Dispose()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            if (Environment.CurrentManagedThreadId != thread) _completed.Task.GetAwaiter().GetResult();
        }
    }
    private long ExecuteSqlStatement(SqlStatement statement, string sql, SqlExecutionOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (statement is SqlCreateIndex index) { CreateSqlIndex(index, sql, options, cancellationToken); return 0; }
            if (statement is SqlDropIndex drop) { DropSqlIndex(drop.Name, options, cancellationToken); return 0; }
            if (statement is SqlCreateView view) { CreateSqlView(view, sql, options, cancellationToken); return 0; }
            if (statement is SqlDropView dropView) { DropSqlView(dropView.Name, options, cancellationToken); return 0; }
            if (statement is SqlAlterTable alter) { AlterSqlTable(alter, options, cancellationToken); return 0; }
            var target = ResolveSqlObject(statement.Table, options, statement is SqlCreateTable);
            if (statement is SqlCreateTable create)
            {
                foreach (var field in create.Definition.PrimaryFormat.Fields)
                    if (field.DefaultValue is { Value: not null } value) _ = SqlFieldValue(field, value.Value);
                using var gate = new JobLockStore(_factory).EnterRecordWrite(cancellationToken);
                CreateFileCore(target.Library, target.Name, PhysicalAttribute, create.Definition, sql, null,
                    cancellationToken: cancellationToken, sqlNaming: options.Naming, sqlOptions: options);
                return 0;
            }
            if (statement is SqlDropTable) { DropSqlTable(target.Library, target.Name, cancellationToken); return 0; }
            return statement switch {
                SqlInsert insert => InsertSqlRows(target.Library, target.Name, insert, options, cancellationToken),
                SqlUpdate or SqlDelete => MutateSqlRows(target.Library, target.Name, statement, options, cancellationToken),
                _ => throw new CpfException("SQL0104", "Unsupported SQL statement.") };
        }
        catch (CpfException error) when (error.MessageId == "CPF5805") { throw new CpfException("SQL0601", error.Message); }
        catch (CpfException error) when (error.MessageId is "CPF9801" or "CPF9810" or "CPF2110" or "CPF2105") { throw new CpfException("SQL0204", error.Message); }
        catch (CpfException error) when (error.MessageId == "CPF9802") { throw new CpfException("SQL0551", error.Message); }
        catch (CpfException error) when (error.MessageId is "SQL0545" or "SQL0546" or "SQL0530" or "SQL0531" or "SQL0532" or "SQL0629" or "SQL0538" or "SQL0539" or "SQL7008" or "SQL0642") { throw SqlDiagnostics.Enrich(error); }
        catch (SqliteException error)
        {
            if (SqlCheckRuntime.DecodeError(error) is { } checkError) throw checkError;
            var id = error.SqliteExtendedErrorCode switch { 1555 or 2067 => "SQL0803", 1299 => "SQL0407", 787 => "SQL0530", 275 => "SQL0545", _ => "SQL0901" };
            var table = statement.Table.Schema is { } schema ? schema + "." + statement.Table.Name : statement.Table.Name;
            var reason = id switch {
                "SQL0803" => "Duplicate key violates a unique constraint", "SQL0407" => "Null value is not allowed",
                "SQL0530" => "Referential constraint failed", "SQL0545" => "Check constraint failed", _ => "SQL operation failed" };
            var failure = new CpfException(id, reason + " for table " + table + ".");
            throw id is "SQL0545" or "SQL0530" or "SQL0531" or "SQL0532" ? SqlDiagnostics.Enrich(failure) : failure;
        }
    }

    private (string Library, string Name) ResolveSqlObject(SqlObjectName name, SqlExecutionOptions options, bool creating)
    {
        if (!Enum.IsDefined(options.Naming)) throw new CpfException("SQL0104", "Invalid SQL naming convention.");
        var library = name.Schema ?? options.DefaultCollection;
        if (library is null)
            library = options.Naming == SqlNaming.Sql ? options.DefaultSchema : creating ? options.CurrentLibrary :
                options.LibraryList.FirstOrDefault(candidate => _objects.GetForAuthorization(candidate, name.Name, FileType) is not null) ?? options.CurrentLibrary;
        library = library.ToUpperInvariant();
        if (!ObjectName.IsValid(library)) throw new CpfException("SQL0104", "Invalid SQL schema.");
        return (ResolveLibrary(library), name.Name);
    }

    private void DropSqlTable(string library, string name, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        if (OperationIdentity.Current?.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("SQL0204", "Table not found.");
        if (definition.Logical is not null || definition.SqlView is not null) throw new CpfException("SQL0104", "DROP TABLE requires a physical table.");
        RequireUnenlisted(library, name, "DROP TABLE");
        new ServiceAuthorization(_factory).RequireObject(library, name, ObjectType.File, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        new JobLockStore(_factory).RequireNoPersistentAllocation(new(library, name, ObjectType.File));
        var dependents = SqlTableLogicalDependents(connection, transaction, library, name);
        foreach (var dependent in dependents)
        {
            new ServiceAuthorization(_factory).RequireObject(dependent.Library, dependent.Name.Value, ObjectType.File, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
            new JobLockStore(_factory).RequireNoPersistentAllocation(new(dependent.Library, dependent.Name.Value, ObjectType.File));
        }
        var foreignChanges = PlanIncomingForeignChanges(connection,transaction,new(library,name),definition,definition,[],new Dictionary<string,bool>(),deleting:true);
        SqlForeignCatalog.NullHooks(connection,transaction,install:false);
        ApplyIncomingForeignChanges(connection,transaction,foreignChanges,token);
        foreach (var dependent in dependents)
            SqliteCancellation.Run(connection, token, () => { ObjectCatalogOperations.DeleteCore(dependent, ObjectType.File, connection, transaction); return 0; });
        SqliteCancellation.Run(connection, token, () => { ObjectCatalogOperations.DeleteCore(new(library, name), ObjectType.File, connection, transaction); return 0; });
        SqlForeignCatalog.NullHooks(connection,transaction,install:true);
        CheckSqlForeignRows(connection,transaction);
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }

    private static IReadOnlyList<QualifiedName> SqlTableLogicalDependents(SqliteConnection connection, SqliteTransaction transaction, string library, string name)
    {
        var result = new List<QualifiedName>(); var visited = new HashSet<QualifiedName>(); var active = new HashSet<QualifiedName>();
        Visit(new(library, name), 0); return result;
        void Visit(QualifiedName target, int depth)
        {
            if (depth > 128) throw new CpfException("SQL0904", "SQL dependency limit exceeded.");
            if (!active.Add(target)) throw new CpfException("SQL0104", "Circular SQL object dependency.");
            if (!visited.Add(target)) { active.Remove(target); return; }
            if (visited.Count > 4001) throw new CpfException("SQL0904", "SQL dependency limit exceeded.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT d.source_lib,d.source_name,d.source_type,f.def FROM sys_object_dependencies d
                LEFT JOIN sys_file_defs f ON f.lib=d.source_lib AND f.name=d.source_name AND f.type=d.source_type
                WHERE d.target_lib=$lib AND d.target_name=$name AND d.target_type='*FILE' ORDER BY d.source_lib,d.source_name LIMIT 4001
                """;
            command.Parameters.AddWithValue("$lib", target.Library); command.Parameters.AddWithValue("$name", target.Name.Value);
            var dependents = new List<QualifiedName>();
            using (var reader = command.ExecuteReader()) while (reader.Read())
            {
                if (dependents.Count >= 4000) throw new CpfException("SQL0904", "SQL dependency limit exceeded.");
                var definition = reader.IsDBNull(3) ? null : FileDefinition.FromJson(reader.GetString(3));
                if (reader.GetString(2) != ObjectType.File || definition is null || definition.Logical is null && definition.SqlView is null)
                    throw new CpfException("IPC0201", "Object has an unsupported registered dependent.");
                dependents.Add(new(reader.GetString(0), reader.GetString(1)));
            }
            foreach (var dependent in dependents) Visit(dependent, depth + 1);
            active.Remove(target);
            if (depth > 0) result.Add(target);
        }
    }

    private long InsertSqlRows(string library, string name, SqlInsert insert, SqlExecutionOptions options, CancellationToken token)
    {
        var root = new QualifiedName(library, name); var now = DateTimeOffset.UtcNow;
        string? member = null, identity = null, fingerprint = null, viewFingerprint = null;
        QualifiedName? physical = null; object?[][]? values = null;
        return WithCapacityRecovery(Insert, token, authorizeTarget: AuthorizeRecovery);

        void AuthorizeRecovery(MemberFullRequest request, SqliteConnection? publisher, SqliteTransaction? publishing)
        {
            if (publisher is null)
            {
                using var metadata = _factory.Open(); using var read = metadata.BeginTransaction(deferred: true);
                AuthorizeRecovery(request, metadata, read); return;
            }
            var current = ReadDefinition(publisher, publishing, root.Library, root.Name.Value) ?? throw new CpfException("SQL0204", "Insert target not found.");
            if (current.SqlView is not null)
            {
                var plan = BindViewWritePlan(publisher, publishing!, root, current, now, token);
                AuthorizeViewWrite(publisher, publishing!, plan, AuthorityBit.Add, AuthorityBit.Add);
                if (plan.Physical.Library != request.Library || plan.Physical.Name.Value != request.File || ViewWriteFingerprint(plan) != viewFingerprint)
                    throw new CpfException("SQL0104", "View changed while awaiting member capacity.");
            }
            else
            {
                new ServiceAuthorization(_factory).RequireBoundObject(publisher, publishing!, root.Library, root.Name.Value, AuthorityBit.ObjectOperate | AuthorityBit.Add);
                if (root.Library != request.Library || root.Name.Value != request.File) throw new CpfException("SQL0104", "Insert binding changed during member recovery.");
            }
        }
        long Insert(CancellationToken cancellation)
        {
            using var gate = new JobLockStore(_factory).EnterRecordWrite(cancellation);
            using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, cancellation);
            ValidateIndexPublisher(connection, transaction, cancellation);
            Authorize(root.Library, root.Name.Value, AuthorityBit.Add);
            var requested = ReadDefinition(connection, transaction, root.Library, root.Name.Value) ?? throw new CpfException("SQL0204", "Table not found.");
            SqlViewWritePlan? view = null; var effective = insert; var definition = requested; var target = root;
            if (requested.SqlView is not null)
            {
                view = BindViewWritePlan(connection, transaction, root, requested, now, cancellation);
                AuthorizeViewWrite(connection, transaction, view, AuthorityBit.Add, AuthorityBit.Add);
                effective = MapViewInsert(view, insert); definition = view.Definition; target = view.Physical;
                if (viewFingerprint is null) viewFingerprint = ViewWriteFingerprint(view);
                else if (viewFingerprint != ViewWriteFingerprint(view)) throw new CpfException("SQL0104", "View changed during capacity recovery.");
            }
            else RequireUnenlisted(root.Library, root.Name.Value, "SQL COMMIT(*NONE)");
            if (definition.Logical is not null || definition.SqlView is not null || definition.Formats.Count != 1)
                throw new CpfException("SQL0104", "SQL INSERT requires a physical table or insertable view.");
            if (physical is null) physical = target; else if (physical != target) throw new CpfException("SQL0104", "Insert base table changed during recovery.");
            var format = definition.PrimaryFormat;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$file AND type='*FILE' ORDER BY created,mbr LIMIT 1";
            command.Parameters.AddWithValue("$lib", target.Library); command.Parameters.AddWithValue("$file", target.Name.Value);
            var first = command.ExecuteScalar() as string ?? throw new CpfException("SQL0204", "Table has no member.");
            if (member is null)
            {
                member = first; identity = RecordSlots.Identity(connection, transaction, target.Library, target.Name.Value, member); fingerprint = CursorFingerprint(definition);
                values = BindSqlRows(effective, definition, cancellation, options.Conversion, now);
            }
            else if (member != first || identity != RecordSlots.Identity(connection, transaction, target.Library, target.Name.Value, member) || fingerprint != CursorFingerprint(definition))
                throw new CpfException("SQL0104", "SQL table changed during capacity recovery; repeat the statement.");
            EnsureExactDecimalStorage(connection, transaction, MemberTable(target.Library, target.Name.Value, member), format);
            command.Parameters.Clear(); var rowid = RowIdentitySql(format);
            command.CommandText = "INSERT INTO " + Quote(MemberTable(target.Library, target.Name.Value, member)) + "(" + Quote(rowid) + "," +
                string.Join(',', format.Fields.Select(field => Quote(field.Name))) + ") VALUES ($rrn," + string.Join(',', format.Fields.Select((_, index) => "$v" + index)) + ")";
            command.Parameters.AddWithValue("$rrn", 0L);
            for (var index = 0; index < format.Fields.Count; index++) command.Parameters.AddWithValue("$v" + index, DBNull.Value);
            foreach (var row in values!)
            {
                cancellation.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint(); CheckViewRow(view, row);
                var number = RecordSlots.Next(connection, transaction, target.Library, target.Name.Value, member, rowid, definition.ReuseDeletedRecords);
                RequireMemberCapacity(connection, transaction, target.Library, target.Name.Value, member, definition, number);
                new JobLockStore(_factory).RequireRecordAccess(target.Library, target.Name.Value, member, number, write: true);
                command.Parameters["$rrn"].Value = number;
                for (var index = 0; index < row.Length; index++) command.Parameters["$v" + index].Value = row[index] ?? DBNull.Value;
                SqliteCancellation.Run(connection, cancellation, command.ExecuteNonQuery);
            }
            if (view is not null) AuthorizeViewWrite(connection, transaction, view, AuthorityBit.Add, AuthorityBit.Add, acquireLocks: false);
            ValidateIndexPublisher(connection, transaction, cancellation); transaction.Commit(); return values.LongLength;
        }
    }
    private static string ViewWriteFingerprint(SqlViewWritePlan plan) => string.Join('\n', plan.Nodes.Select(node => node.Key + "\0" + node.Owner + "\0" + node.Definition));

    private static object?[][] BindSqlRows(SqlInsert insert, FileDefinition definition, CancellationToken token, SqlConversionOptions? conversion = null, DateTimeOffset? statementTime = null)
    {
        var fields = definition.PrimaryFormat.Fields;
        var columns = insert.Columns ?? fields.Select(field => field.Name).ToArray();
        foreach (var name in columns) if (fields.All(field => field.Name != name)) throw new CpfException("SQL0206", "Unknown INSERT column " + name + ".");
        if ((long)insert.Rows.Count * fields.Count > 1000000) throw new CpfException("SQL0904", "SQL VALUES exceeds one million normalized cells.");
        var result = new object?[insert.Rows.Count][]; var now = statementTime ?? DateTimeOffset.UtcNow; long bytes = 0;
        for (var index = 0; index < insert.Rows.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var row = insert.Rows[index];
            if (row.Count != 0 && row.Count != columns.Count) throw new CpfException("SQL0117", "INSERT column and value counts differ.");
            var supplied = row.Count == 0 ? new Dictionary<string, SqlValue>() : columns.Select((name, ordinal) => (name, value: row[ordinal])).ToDictionary(item => item.name, item => item.value);
            result[index] = new object?[fields.Count];
            for (var ordinal = 0; ordinal < fields.Count; ordinal++)
            {
                var field = fields[ordinal]; var value = supplied.GetValueOrDefault(field.Name, new(SqlValueKind.Default));
                object? raw;
                if (value.Kind == SqlValueKind.Default)
                {
                    if (definition.SqlTable is not null && !field.NullCapable && field.DefaultValue is null && !field.CurrentDatetimeDefault)
                        throw new CpfException("SQL0407", "Column " + field.Name + " requires a value.");
                    raw = field.CurrentDatetimeDefault ? SqlNow(field.Type, now) : DefaultFor(field);
                }
                else raw = value.Kind switch {
                    SqlValueKind.CurrentDate => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    SqlValueKind.CurrentTime => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    SqlValueKind.CurrentTimestamp => now.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), _ => value.Value };
                var normalized = SqlFieldValue(field, raw, value.Kind == SqlValueKind.Literal ? conversion : null); result[index][ordinal] = normalized;
                bytes += normalized is string text ? Encoding.UTF8.GetByteCount(text) : 16;
                if (bytes > 67108864) throw new CpfException("SQL0904", "SQL VALUES exceeds 64 MiB of normalized values.");
            }
        }
        return result;
    }

}
