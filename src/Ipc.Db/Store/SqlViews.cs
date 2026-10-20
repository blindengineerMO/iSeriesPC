using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record BoundSqlView(QualifiedName Target, FileDefinition Definition, ObjectDescriptor Descriptor, bool Exists);
    private BoundSqlView BindSqlView(SqliteConnection connection, SqliteTransaction transaction, SqlCreateView view, SqlExecutionOptions options, CancellationToken token)
    {
        var physical = new HashSet<QualifiedName>();
        var plan = ResolveQueryPlan(connection, transaction, view.Query, options, token,
            (library, name, _) => Authorize(library, name, AuthorityBit.Read), physical);
        var target = ResolveSqlObject(view.Name, options, true);
        if (view.Name.Schema is null && options.Naming == SqlNaming.System && options.DefaultCollection is null)
            target = (plan.Tables[0].Library, view.Name.Name);
        var temporary = new JobTemporaryLibrary(_factory);
        if (plan.Tables.Any(table => temporary.IsTemporary(table.Library) && table.Library != target.Library) ||
            physical.Any(file => temporary.IsTemporary(file.Library) && file.Library != target.Library))
            throw new CpfException("SQL0104", "A permanent view cannot depend on private QTEMP data.");
        foreach (var source in physical) RequireUnenlisted(source.Library, source.Name.Value, "CREATE VIEW");
        if (physical.Count == 1)
        { var source = physical.Single(); new ServiceAuthorization(_factory).RequireObject(source.Library, source.Name.Value, FileType, AuthorityBit.ObjectManagement); }
        var previous = ReadDefinition(connection, transaction, target.Library, target.Name);
        var descriptor = _objects.GetForAuthorization(target.Library, target.Name, FileType);
        if (descriptor is not null)
        {
            if (!view.Replace) throw new CpfException("SQL0601", "View object already exists.");
            if (previous?.SqlView is null) throw new CpfException("SQL0104", "OR REPLACE requires an SQL-created view.");
            var authorization = new ServiceAuthorization(_factory);
            try { authorization.RequireObject(target.Library, target.Name, FileType, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist); }
            catch (CpfException error) when (error.MessageId == "CPF9802")
            { authorization.RequireObject(target.Library, target.Name, FileType, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement | AuthorityBit.Read); }
        }
        RequireLogicalSqlCreation(connection, transaction, target.Library);
        var columns = new List<SqlQueryColumn>(); var compilers = new List<SqlQueryCompiler>();
        try
        {
            var generated = CompileQueryPlan(plan, connection, [], new(), token, compilers);
            using var prepare = connection.CreateCommand(); prepare.Transaction = transaction; prepare.CommandText = generated; prepare.Prepare();
            columns.AddRange(compilers[^1].Columns);
        }
        finally { foreach (var compiler in compilers.AsEnumerable().Reverse()) compiler.Dispose(); }
        if (view.Columns is not null && view.Columns.Count != columns.Count) throw new CpfException("SQL0117", "View column list does not match the SELECT result.");
        var names = view.Columns ?? columns.Select(column => column.Name).ToArray();
        if (names.Any(name => !ObjectName.IsValid(name)) || view.Columns is null && columns.Any(column => column.Calculated))
            throw new CpfException("SQL0104", "View expressions require unique, named result columns.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new CpfException("SQL0612", "Duplicate view column names.");
        columns = columns.Select((column, index) => column with { Name = names[index], Calculated = false }).ToList();
        var format = ViewRecordFormat(view.Format ?? target.Name, columns);
        var stored = new SqlViewDefinition(view.QueryText, options, plan.Tables.Select(table =>
            new SqlViewSource(table.Library, table.Name, table.Definition.PrimaryFormat.Fields.Select(field => field.Name).ToArray())).ToArray(), columns, view.CheckOption);
        var definition = new FileDefinition { Name = target.Name, Attribute = FileAttribute.Logical, Formats = [format], SqlView = stored,
            Ccsid = columns.FirstOrDefault(column => column.Type.Kind is SqlQueryKind.Character or SqlQueryKind.VarChar)?.Type.Ccsid ?? options.Ccsid };
        var created = new ObjectDescriptor { Key = new(target.Library, target.Name), ObjectType = FileType, Attribute = "*LF", Format = format.Name, Owner = _owner, Ccsid = definition.Ccsid };
        CreateSqlTableDescriptor(connection, transaction, created, options.Naming, validateOnly: true, replacing: descriptor);
        // SQL ownership may select a schema profile distinct from the creator.
        // Check that owner's direct/indirect source rights without adopting the reader.
        foreach (var table in AllQueryTables(plan)) new ServiceAuthorization(_factory).RequireViewSource(created.Owner, table.Library, table.Name, Authorities.UseBits);
        foreach (var source in physical) new ServiceAuthorization(_factory).RequireViewSource(created.Owner, source.Library, source.Name.Value, Authorities.UseBits);
        ValidateViewCheckOption(connection, transaction, created.Key, definition, created.Owner, token);
        return new(new(target.Library, target.Name), definition, created, descriptor is not null);
    }
    private static IEnumerable<QueryTable> AllQueryTables(QueryPlan plan) => plan.Tables.SelectMany(table =>
        table.View is null ? new[] { table } : new[] { table }.Concat(AllQueryTables(table.View)));

    private void RequireLogicalSqlCreation(SqliteConnection connection, SqliteTransaction transaction, string library)
    {
        if (new CommandDefinitionStore(_factory).Load("QSYS", "CRTLF").Builtin != "CRTLF") throw new CpfException("CPF9802", "CRTLF adapter changed.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sys_objects WHERE lib=$lib AND type='*DTADCT'"; command.Parameters.AddWithValue("$lib", library);
        var dictionaries = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) dictionaries.Add(reader.GetString(0));
        foreach (var dictionary in dictionaries) new ServiceAuthorization(_factory).RequireObject(library, dictionary, ObjectType.DataDictionary, Authorities.ChangeBits);
    }
    private void CreateSqlView(SqlCreateView view, string sql, SqlExecutionOptions options, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var bound = BindSqlView(connection, transaction, view, options, token); var target = bound.Target;
        if (bound.Exists)
        {
            var locks = new JobLockStore(_factory); locks.RequireNoPersistentAllocation(new(target.Library, target.Name.Value, FileType));
            foreach (var dependent in SqlTableLogicalDependents(connection, transaction, target.Library, target.Name.Value))
                locks.RequireNoPersistentAllocation(new(dependent.Library, dependent.Name.Value, FileType));
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE sys_objects SET owner=$owner,format=$format,source=$source,ccsid=$ccsid,description=NULL,changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*FILE'; DELETE FROM sys_object_dependencies WHERE source_lib=$lib AND source_name=$name AND source_type='*FILE'; DELETE FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'";
            BindViewParameters(update, bound, sql); update.ExecuteNonQuery();
        }
        else { bound.Descriptor.Source = sql; CreateSqlTableDescriptor(connection, transaction, bound.Descriptor, options.Naming); }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = (bound.Exists ? "" : "INSERT INTO sys_file_defs VALUES($lib,$name,'*FILE',$def);") +
                "INSERT INTO sys_file_members(lib,name,type,mbr,created) VALUES($lib,$name,'*FILE',$name,$now)";
            BindViewParameters(insert, bound, sql); insert.ExecuteNonQuery();
        }
        foreach (var source in bound.Definition.SqlView!.Sources.DistinctBy(source => (source.Library, source.Name)))
        {
            using var dependency = connection.CreateCommand(); dependency.Transaction = transaction;
            dependency.CommandText = "INSERT INTO sys_object_dependencies VALUES($lib,$name,'*FILE',$sl,$sn,'*FILE')";
            dependency.Parameters.AddWithValue("$lib", target.Library); dependency.Parameters.AddWithValue("$name", target.Name.Value);
            dependency.Parameters.AddWithValue("$sl", source.Library); dependency.Parameters.AddWithValue("$sn", source.Name); dependency.ExecuteNonQuery();
        }
        // Resolve the published definition and every dependent before committing a replacement.
        ValidatePublishedViews(connection, transaction, target, options, token);
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }
    private static void BindViewParameters(SqliteCommand command, BoundSqlView view, string sql)
    {
        command.Parameters.AddWithValue("$lib", view.Target.Library); command.Parameters.AddWithValue("$name", view.Target.Name.Value);
        command.Parameters.AddWithValue("$def", view.Definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$owner", view.Descriptor.Owner); command.Parameters.AddWithValue("$format", view.Descriptor.Format!); command.Parameters.AddWithValue("$source", sql);
        command.Parameters.AddWithValue("$ccsid", view.Definition.Ccsid);
    }
    private void ValidatePublishedViews(SqliteConnection connection, SqliteTransaction transaction, QualifiedName target, SqlExecutionOptions options, CancellationToken token)
    {
        var views = SqlTableLogicalDependents(connection, transaction, target.Library, target.Name.Value).Append(target);
        foreach (var key in views)
        {
            var definition = ReadDefinition(connection, transaction, key.Library, key.Name.Value); if (definition?.SqlView is null) continue;
            ValidateViewCheckOption(connection, transaction, key, definition, ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner, token);
            var source = new SqlTableSource(new(key.Library, key.Name.Value)); var query = new SqlSelect([new(new SqlStar())], source, null, [], null, [], null, false, 0);
            var plan = ResolveQueryPlan(connection, transaction, query, options, token, (_, _, _) => { }, []);
            var compilers = new List<SqlQueryCompiler>();
            try { _ = CompileQueryPlan(plan, connection, [], new(), token, compilers); }
            finally { foreach (var compiler in compilers.AsEnumerable().Reverse()) compiler.Dispose(); }
        }
    }
    private static RecordFormat ViewRecordFormat(string name, IReadOnlyList<SqlQueryColumn> columns)
    {
        var format = new RecordFormat { Name = name };
        foreach (var column in columns)
        {
            var type = column.Type;
            var (kind, length) = type.Kind switch {
                SqlQueryKind.SmallInt => (FieldType.Binary, 2), SqlQueryKind.Integer => (FieldType.Binary, 4), SqlQueryKind.BigInt => (FieldType.Binary, 8),
                SqlQueryKind.Decimal => (FieldType.Packed, type.Precision), SqlQueryKind.Numeric => (FieldType.Zoned, type.Precision),
                SqlQueryKind.Real => (FieldType.Float, 4), SqlQueryKind.Double => (FieldType.Float, 8), SqlQueryKind.Boolean => (FieldType.Logic, 1),
                SqlQueryKind.Date => (FieldType.Date, 10), SqlQueryKind.Time => (FieldType.Time, 8), SqlQueryKind.Timestamp => (FieldType.Timestamp, 26),
                SqlQueryKind.Character or SqlQueryKind.VarChar => (FieldType.Alpha, Math.Max(1, type.Length)), SqlQueryKind.Null => (FieldType.Alpha, 1),
                _ => throw new CpfException("SQL0104", "Unsupported view result type.") };
            format.Fields.Add(new() { Name = column.Name, Type = kind, Length = length, Decimals = type.Scale, Ccsid = type.Ccsid,
                VariableLength = type.Kind == SqlQueryKind.VarChar, NullCapable = type.Nullable || type.Kind == SqlQueryKind.Null });
        }
        format.AssignPositions(); if (format.RecordLength > 32766) throw new CpfException("SQL0904", "View external record exceeds the implemented byte limit.");
        return format;
    }
    private QualifiedName RequireSqlView(SqliteConnection connection, SqliteTransaction transaction, SqlObjectName name, SqlExecutionOptions options)
    {
        var target = ResolveSqlObject(name, options, false);
        var definition = ReadDefinition(connection, transaction, target.Library, target.Name) ?? throw new CpfException("SQL0204", "View not found.");
        if (definition.SqlView is null) throw new CpfException("SQL0104", "DROP VIEW requires an SQL-created view.");
        new ServiceAuthorization(_factory).RequireObject(target.Library, target.Name, FileType, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        RequireViewSourcesUnenlisted(connection, transaction, new(target.Library, target.Name), "DROP VIEW");
        return new(target.Library, target.Name);
    }
    private IReadOnlyList<QualifiedName> RequireViewSourcesUnenlisted(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root, string operation)
    {
        var pending = new Stack<QualifiedName>(); var seen = new HashSet<QualifiedName>(); var physical = new List<QualifiedName>(); pending.Push(root);
        while (pending.TryPop(out var key))
        {
            if (!seen.Add(key)) continue;
            if (seen.Count > 256) throw new CpfException("SQL0904", "View exceeds its source-reference limit.");
            var definition = ReadDefinition(connection, transaction, key.Library, key.Name.Value);
            if (definition?.SqlView is { } view) foreach (var source in view.Sources) pending.Push(new(source.Library, source.Name));
            else if (definition?.Logical is { } logical) pending.Push(new(logical.SourceLibrary, logical.SourceFile));
            else { RequireUnenlisted(key.Library, key.Name.Value, operation); physical.Add(key); }
        }
        return physical;
    }
    private void DropSqlView(SqlObjectName name, SqlExecutionOptions options, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var target = RequireSqlView(connection, transaction, name, options); var dependents = SqlTableLogicalDependents(connection, transaction, target.Library, target.Name.Value);
        var locks = new JobLockStore(_factory);
        foreach (var key in dependents.Append(target))
        {
            new ServiceAuthorization(_factory).RequireObject(key.Library, key.Name.Value, FileType, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
            locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
        }
        foreach (var key in dependents.Append(target)) ObjectCatalogOperations.DeleteCore(key, FileType, connection, transaction);
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }
}
