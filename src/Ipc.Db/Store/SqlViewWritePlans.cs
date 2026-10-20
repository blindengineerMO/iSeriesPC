using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record ViewWriteNode(QualifiedName Key, string Definition, string Owner);
    private sealed record ViewGrant(QualifiedName Key, string Owner);
    private sealed record ViewCondition(SqlBound Predicate, bool Stable);
    private sealed record SqlViewWritePlan(QualifiedName Root, QualifiedName Physical, FileDefinition Definition,
        IReadOnlyDictionary<string, SqlBound> Columns, IReadOnlyDictionary<string, FieldSpec> Writable,
        IReadOnlySet<string> Unstable, IReadOnlyList<ViewCondition> Filters, IReadOnlyList<ViewCondition> Checks,
        IReadOnlyList<ViewWriteNode> Nodes, IReadOnlyList<ViewGrant> Readers, IReadOnlyList<ViewGrant> Writers,
        IReadOnlyList<QualifiedName> Sources);

    private SqlViewWritePlan BindViewWritePlan(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root,
        FileDefinition definition, DateTimeOffset now, CancellationToken token, string? newOwner = null)
    {
        var active = new HashSet<QualifiedName>(); return Bind(root, definition, 0, newOwner);
        SqlViewWritePlan Bind(QualifiedName key, FileDefinition current, int depth, string? ownerOverride = null)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= 32 || !active.Add(key)) throw new CpfException("SQL0904", "View update path is recursive or too deep.");
            try
            {
                if (current.Formats.Count != 1) throw new CpfException("SQL0150", "View mutation requires single-format source objects.");
                var descriptor = ownerOverride is null ? ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType) : null;
                var owner = ownerOverride ?? descriptor!.Owner;
                var node = new ViewWriteNode(key, current.ToJson(), owner);
                if (current.SqlView is not { } view)
                {
                    if (current.Attribute != FileAttribute.Physical || current.Logical is not null)
                        throw new CpfException("SQL0150", "This view update path is not an implemented physical table.");
                    var fields = current.PrimaryFormat.Fields;
                    return new(root, key, current, fields.Select((field, index) => (field.Name, Value: SqlBinder.Column(field, index))).ToDictionary(item => item.Name, item => item.Value),
                        fields.ToDictionary(field => field.Name), new HashSet<string>(), [], [], [node], [], [], []);
                }
                if (!Enum.IsDefined(view.CheckOption)) throw new CpfException("SQL0104", "Invalid view check option.");
                var query = BoundViewQuery(view);
                if (query.From is not SqlTableSource source || query.Distinct || query.GroupBy.Count != 0 || query.Having is not null || query.Columns.Any(column => ViewHasAggregate(column.Value)))
                    throw new CpfException("SQL0150", "This view is not deletable or updatable.");
                var reference = view.Sources.Single(); var sourceKey = new QualifiedName(reference.Library, reference.Name);
                var sourceDefinition = ReadDefinition(connection, transaction, sourceKey.Library, sourceKey.Name.Value) ?? throw new CpfException("SQL0204", "View source not found.");
                var lower = Bind(sourceKey, sourceDefinition, depth + 1);
                var qualifier = source.Alias ?? source.Table.Name;
                var binder = new SqlBinder(sourceDefinition.PrimaryFormat, qualifier, view.Options.Ccsid, now,
                    column => ResolveViewColumn(lower, column, qualifier), conversion: view.Options.Conversion);
                if (query.Columns.Count != view.Columns.Count) throw new CpfException("SQL0104", "View column shape changed.");
                var columns = new Dictionary<string, SqlBound>(); var writable = new Dictionary<string, FieldSpec>(); var unstable = new HashSet<string>();
                for (var ordinal = 0; ordinal < query.Columns.Count; ordinal++)
                {
                    var expression = query.Columns[ordinal].Value; var name = view.Columns[ordinal].Name;
                    var bound = binder.Bind(expression);
                    if (SqlQueryCompiler.PublicType(bound) != view.Columns[ordinal].Type) throw new CpfException("SQL0104", "View result type changed.");
                    columns.Add(name, bound);
                    if (!ViewExpressionStable(expression, lower.Unstable)) unstable.Add(name);
                    if (expression is SqlColumn direct && lower.Writable.TryGetValue(direct.Name, out var field)) writable.Add(name, field);
                }
                var filters = lower.Filters.ToList();
                if (query.Where is { } where)
                { var predicate = binder.Bind(where); SqlBinder.Predicate(predicate); filters.Add(new(predicate, ViewExpressionStable(where, lower.Unstable))); }
                var checks = lower.Checks.ToList();
                if (view.CheckOption != SqlViewCheckOption.None)
                {
                    if (view.CheckOption == SqlViewCheckOption.Cascaded) checks.AddRange(filters);
                    else if (query.Where is not null) checks.Add(filters[^1]);
                    if (checks.Any(condition => !condition.Stable)) throw new CpfException("SQL0160", "CHECK OPTION cannot depend on a special register or unstable predicate.");
                }
                var sources = lower.Sources.Prepend(sourceKey).Distinct().ToArray();
                return new(root, lower.Physical, lower.Definition, columns, writable, unstable, filters, checks,
                    new[] { node }.Concat(lower.Nodes).ToArray(), lower.Readers.Concat(sources.Select(item => new ViewGrant(item, owner))).Distinct().ToArray(),
                    lower.Writers.Append(new(sourceKey, owner)).Distinct().ToArray(), sources);
            }
            finally { active.Remove(key); }
        }
    }
    private void ValidateViewCheckOption(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, FileDefinition definition, string owner, CancellationToken token)
    {
        if (definition.SqlView?.CheckOption is null or SqlViewCheckOption.None) return;
        try { _ = BindViewWritePlan(connection, transaction, key, definition, DateTimeOffset.UtcNow, token, owner); }
        catch (CpfException error) when (error.MessageId == "SQL0150")
        { throw SqlDiagnostics.Enrich(new("SQL0160", "CHECK OPTION is not allowed for this view.")); }
    }
    private void ValidateViewDml(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, FileDefinition definition,
        SqlStatement statement, SqlExecutionOptions options, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow; var plan = BindViewWritePlan(connection, transaction, key, definition, now, token);
        var operation = statement is SqlInsert ? AuthorityBit.Add : statement is SqlUpdate ? AuthorityBit.Update : AuthorityBit.Delete;
        var permission = operation;
        if (statement is SqlInsert insert) _ = BindSqlRows(MapViewInsert(plan, insert), plan.Definition, token, options.Conversion, now);
        else
        {
            if (statement is SqlUpdate && plan.Writable.Count == 0) throw new CpfException("SQL0150", "View has no updatable columns.");
            var qualifier = (statement as SqlUpdate)?.Alias ?? (statement as SqlDelete)?.Alias ?? key.Name.Value;
            var (predicate, assignments) = BindSqlMutation(definition, statement, options, key.Name.Value,
                column => ResolveViewColumn(plan, column, qualifier),
                column => plan.Writable.TryGetValue(column, out var field) ? field : throw new CpfException("SQL0151", "View column is not updatable."), plan.Definition, now);
            if (predicate?.Reads == true || assignments.Any(item => item.Value.Reads)) permission |= AuthorityBit.Read;
        }
        AuthorizeViewWrite(connection, transaction, plan, permission, operation);
        using var member = connection.CreateCommand(); member.Transaction = transaction;
        member.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
        member.Parameters.AddWithValue("$lib", plan.Physical.Library); member.Parameters.AddWithValue("$name", plan.Physical.Name.Value);
        if (Convert.ToInt64(member.ExecuteScalar()) == 0) throw new CpfException("SQL0204", "View base table has no member.");
    }
    private static SqlBound ResolveViewColumn(SqlViewWritePlan plan, SqlColumn column, string qualifier)
    {
        if (column.Qualifier is not null && column.Qualifier != qualifier || !plan.Columns.TryGetValue(column.Name, out var value))
            throw new CpfException("SQL0206", "Unknown view column or qualifier " + column.Name + ".");
        return value with { Reads = true };
    }
    private static IEnumerable<SqlExpression> ViewChildren(SqlExpression expression) => expression switch {
        SqlUnary value => [value.Value], SqlBinary value => [value.Left, value.Right], SqlIsNull value => [value.Value],
        SqlIn value => new[] { value.Value }.Concat(value.Values), SqlCoalesce value => value.Values, SqlFunction value => value.Arguments,
        SqlCast value => [value.Value], SqlExtract value => [value.Value], SqlDuration value => [value.Value],
        SqlLike value => value.Escape is null ? [value.Value, value.Pattern] : [value.Value, value.Pattern, value.Escape], _ => [] };
    private static bool ViewHasAggregate(SqlExpression expression) => expression is SqlFunction { Name: "COUNT" or "COUNT_BIG" or "SUM" or "AVG" or "MIN" or "MAX" } || ViewChildren(expression).Any(ViewHasAggregate);
    private static bool ViewExpressionStable(SqlExpression expression, IReadOnlySet<string> unstable, int depth = 0)
    {
        if (depth > 128) throw new CpfException("SQL0904", "View expression depth exceeded.");
        return expression is not SqlConstant { Value.Kind: SqlValueKind.CurrentDate or SqlValueKind.CurrentTime or SqlValueKind.CurrentTimestamp } &&
            (expression is not SqlColumn column || !unstable.Contains(column.Name)) && ViewChildren(expression).All(child => ViewExpressionStable(child, unstable, depth + 1));
    }
    private void AuthorizeViewWrite(SqliteConnection connection, SqliteTransaction transaction, SqlViewWritePlan plan,
        AuthorityBit callerPermission, AuthorityBit operation, bool acquireLocks = true)
    {
        var authorization = new ServiceAuthorization(_factory);
        authorization.RequireBoundObject(connection, transaction, plan.Root.Library, plan.Root.Name.Value, AuthorityBit.ObjectOperate | callerPermission);
        foreach (var grant in plan.Readers) authorization.RequireBoundObject(connection, transaction, grant.Key.Library, grant.Key.Name.Value, Authorities.UseBits, grant.Owner);
        foreach (var grant in plan.Writers) authorization.RequireBoundObject(connection, transaction, grant.Key.Library, grant.Key.Name.Value, AuthorityBit.ObjectOperate | operation, grant.Owner);
        foreach (var node in plan.Nodes)
        {
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT f.def,o.owner FROM sys_file_defs f JOIN sys_objects o USING(lib,name,type) WHERE f.lib=$lib AND f.name=$name AND f.type='*FILE'";
            read.Parameters.AddWithValue("$lib", node.Key.Library); read.Parameters.AddWithValue("$name", node.Key.Name.Value);
            using var reader = read.ExecuteReader();
            if (!reader.Read() || FileDefinition.FromJson(reader.GetString(0)).ToJson() != node.Definition || reader.GetString(1) != node.Owner)
                throw new CpfException("SQL0104", "View mutation definition or ownership changed.");
        }
        RequireUnenlisted(plan.Physical.Library, plan.Physical.Name.Value, "SQL view COMMIT(*NONE)");
        if (!acquireLocks) return;
        var locks = new JobLockStore(_factory); locks.RequireAccess(plan.Root.Library, plan.Root.Name.Value, FileType, callerPermission);
        foreach (var grant in plan.Writers) locks.RequireAccess(grant.Key.Library, grant.Key.Name.Value, FileType, operation);
    }
    private static void CheckViewRow(SqlViewWritePlan? plan, object?[] values)
    {
        if (plan is not null && plan.Checks.Any(check => check.Predicate.Evaluate(values) is not true))
            throw SqlDiagnostics.Enrich(new("SQL0161", "The resulting row does not satisfy the view CHECK OPTION."));
    }
    private static SqlInsert MapViewInsert(SqlViewWritePlan plan, SqlInsert insert)
    {
        if (plan.Writable.Count == 0) throw new CpfException("SQL0150", "View has no insertable columns.");
        var columns = insert.Columns ?? plan.Columns.Keys.ToArray(); var mapped = new List<string>();
        foreach (var column in columns)
        {
            if (!plan.Columns.ContainsKey(column)) throw new CpfException("SQL0206", "Unknown view insert column " + column + ".");
            if (!plan.Writable.TryGetValue(column, out var field)) throw new CpfException("SQL0151", "View column " + column + " is not updatable.");
            if (mapped.Contains(field.Name)) throw new CpfException("SQL0612", "A base column is specified more than once through the view.");
            mapped.Add(field.Name);
        }
        return new(new(plan.Physical.Library, plan.Physical.Name.Value), mapped, insert.Rows);
    }
}
