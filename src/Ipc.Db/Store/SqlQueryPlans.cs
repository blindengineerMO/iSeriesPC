using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record QueryPlan(SqlSelect Query, IReadOnlyList<QueryTable> Tables, SqlExecutionOptions Options);
    private sealed class QueryResolutionBudget { internal int References; }

    private QueryPlan ResolveQueryPlan(SqliteConnection metadata, SqliteTransaction snapshot, SqlSelect query, SqlExecutionOptions options,
        CancellationToken token, Action<string, string, string?> access, HashSet<QualifiedName> physical,
        IReadOnlyList<string>? viewOwners = null, HashSet<QualifiedName>? ancestry = null, QueryResolutionBudget? budget = null,
        IReadOnlyDictionary<QualifiedName, FileDefinition>? definitions = null)
    {
        ancestry ??= []; budget ??= new(); viewOwners ??= []; var tables = new List<QueryTable>();
        Resolve(query.From, false);
        if (tables.GroupBy(table => table.Qualifier).Any(group => group.Count() > 1)) throw new CpfException("SQL0203", "Query table qualifiers must be distinct.");
        return new(query, tables, options);

        void Resolve(SqlFrom from, bool nullable)
        {
            token.ThrowIfCancellationRequested();
            if (from is SqlJoinSource join)
            { Resolve(join.Left, nullable || join.Kind is "RIGHT" or "FULL"); Resolve(join.Right, nullable || join.Kind is "LEFT" or "FULL"); return; }
            if (++budget.References > 256 || tables.Count >= 32) throw new CpfException("SQL0904", "Query exceeds its expanded table-reference limit.");
            var source = (SqlTableSource)from; var target = ResolveSqlObject(source.Table, options, false);
            Access(target.Library, target.Name);
            var definition = Definition(target.Library, target.Name) ?? throw new CpfException("SQL0204", "Query table not found.");
            if (definition.Formats.Count != 1) throw new CpfException("SQL0104", "Queries require single-format files.");
            using var member = metadata.CreateCommand(); member.Transaction = snapshot;
            member.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
            member.Parameters.AddWithValue("$lib", target.Library); member.Parameters.AddWithValue("$name", target.Name);
            var name = member.ExecuteScalar() as string ?? throw new CpfException("SQL0204", "Query file has no member.");
            QueryPlan? nested = null; string? logicalSql = null; string? owner = null;
            if (definition.SqlView is { } view)
            {
                var key = new QualifiedName(target.Library, target.Name);
                if (ancestry.Count >= 32 || !ancestry.Add(key)) throw new CpfException("SQL0904", "Recursive or excessively nested view definition.");
                try
                {
                    var descriptor = ObjectSigningService.Read(metadata, snapshot, target.Library, target.Name, FileType) ?? throw new CpfException("SQL0204", "View object not found.");
                    owner = descriptor.Owner;
                    nested = ResolveQueryPlan(metadata, snapshot, BoundViewQuery(view), view.Options, token, access, physical,
                        viewOwners.Append(descriptor.Owner).Distinct(StringComparer.Ordinal).ToArray(), ancestry, budget, definitions);
                }
                finally { ancestry.Remove(key); }
            }
            else if (definition.Logical is { } logical)
            {
                ValidateLogical(logical); Access(logical.SourceLibrary, logical.SourceFile);
                var parent = Definition(logical.SourceLibrary, logical.SourceFile) ?? throw new CpfException("SQL0204", "Query source not found.");
                if (parent.Logical is not null || parent.SqlView is not null || parent.Formats.Count != 1 || parent.PrimaryFormat.Name != logical.SourceFormat ||
                    definition.PrimaryFormat.Fields.Any(field => parent.PrimaryFormat.Find(field.Name) is not { } original || original.Type != field.Type || original.Length != field.Length || original.Decimals != field.Decimals || original.Ccsid != field.Ccsid || original.NullCapable != field.NullCapable || original.VariableLength != field.VariableLength))
                    throw new CpfException("SQL0104", "Logical query format changed; recompile the file.");
                if (!logical.Members.TryGetValue(name, out var members)) throw new CpfException("SQL0204", "Logical query member has no binding.");
                foreach (var physicalMember in members)
                {
                    using var exists = metadata.CreateCommand(); exists.Transaction = snapshot;
                    exists.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$file AND type='*FILE' AND mbr=$member";
                    exists.Parameters.AddWithValue("$lib", logical.SourceLibrary); exists.Parameters.AddWithValue("$file", logical.SourceFile); exists.Parameters.AddWithValue("$member", physicalMember);
                    if (Convert.ToInt64(exists.ExecuteScalar()) != 1) throw new CpfException("SQL0204", "Logical source member not found.");
                }
                using var predicate = metadata.CreateCommand(); predicate.Transaction = snapshot;
                var filter = LogicalPredicate(predicate, logical, parent.PrimaryFormat, inlineConstants: true);
                var fields = string.Join(',', definition.PrimaryFormat.Fields.Select(field => Quote(field.Name)));
                logicalSql = "(" + (members.Count == 0 ? "SELECT " + string.Join(',', definition.PrimaryFormat.Fields.Select(field => "NULL AS " + Quote(field.Name))) + " WHERE 0" :
                    string.Join(" UNION ALL ", members.Select(physicalMember => "SELECT " + fields + " FROM " + Quote(MemberTable(logical.SourceLibrary, logical.SourceFile, physicalMember)) + " WHERE " + filter))) + ")";
                physical.Add(new(logical.SourceLibrary, logical.SourceFile));
            }
            else if (definition.Attribute is FileAttribute.Physical or FileAttribute.Source) physical.Add(new(target.Library, target.Name));
            else throw new CpfException("SQL0104", "Query file has no implemented definition.");
            tables.Add(new(source, target.Library, target.Name, name, definition, tables.Count) { Nullable = nullable, LogicalSql = logicalSql, View = nested, ViewOwner = owner });
        }
        FileDefinition? Definition(string library, string name) => definitions is not null && definitions.TryGetValue(new(library, name), out var definition) ?
            definition : ReadDefinition(metadata, snapshot, library, name);
        void Access(string library, string name)
        {
            if (viewOwners.Count == 0) access(library, name, null);
            else foreach (var owner in viewOwners) access(library, name, owner);
        }
    }

    private static SqlSelect BoundViewQuery(SqlViewDefinition view)
    {
        if (view.Query is null || view.Options is null || view.Sources is null || view.Columns is null || !Enum.IsDefined(view.CheckOption) ||
            view.Query.Length > 2097152 || view.Sources.Count is < 1 or > 32 || view.Columns.Count is < 1 or > 1000 ||
            view.Sources.Any(source => source is null || source.Columns is null) ||
            view.Columns.Any(column => column is null || column.Type is null || !ObjectName.IsValid(column.Name)) ||
            view.Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != view.Columns.Count)
            throw new CpfException("SQL0104", "Invalid stored view definition.");
        var query = SqlStatementParser.Parse(view.Query, view.Options) as SqlSelect ?? throw new CpfException("SQL0104", "A view must contain SELECT.");
        if (query.ParameterCount != 0 || query.OrderBy.Count != 0 || query.FetchFirst is not null) throw new CpfException("SQL0104", "Invalid view query clauses.");
        var ordinal = 0; var columns = new List<SqlProjection>();
        var from = Rewrite(query.From); if (ordinal != view.Sources.Count) throw new CpfException("SQL0104", "View reference count changed.");
        foreach (var column in query.Columns)
        {
            if (column.Value is not SqlStar star) { columns.Add(column); continue; }
            var original = QuerySourceList(query.From).Select((source, index) => (Qualifier: source.Alias ?? source.Table.Name, Source: view.Sources[index])).Where(item => star.Qualifier is null || item.Qualifier == star.Qualifier).ToArray();
            if (original.Length == 0) throw new CpfException("SQL0206", "Unknown view wildcard qualifier.");
            foreach (var item in original) columns.AddRange(item.Source.Columns.Select(name => new SqlProjection(new SqlColumn(name, item.Qualifier))));
        }
        return query with { From = from, Columns = columns };

        SqlFrom Rewrite(SqlFrom source)
        {
            if (source is SqlJoinSource join) return join with { Left = Rewrite(join.Left), Right = Rewrite(join.Right) };
            if (ordinal >= view.Sources.Count) throw new CpfException("SQL0104", "View reference count changed.");
            var original = (SqlTableSource)source; var target = view.Sources[ordinal++];
            if (!ObjectName.IsValid(target.Library) || !ObjectName.IsValid(target.Name) || target.Columns.Count > 1000 || target.Columns.Any(name => !ObjectName.IsValid(name)))
                throw new CpfException("SQL0104", "Invalid stored view source.");
            return original with { Table = new(target.Library, target.Name), Alias = original.Alias ?? original.Table.Name };
        }
    }
    private static IEnumerable<SqlTableSource> QuerySourceList(SqlFrom source) => source is SqlTableSource table ? [table] :
        QuerySourceList(((SqlJoinSource)source).Left).Concat(QuerySourceList(((SqlJoinSource)source).Right));

    private static string CompileQueryPlan(QueryPlan plan, SqliteConnection connection, IReadOnlyList<SqlQueryParameter> parameters,
        SqlQueryLimits limits, CancellationToken token, List<SqlQueryCompiler> compilers, QueryBudget? budget = null, DateTimeOffset? statementTime = null)
    {
        budget ??= new(limits); statementTime ??= DateTimeOffset.UtcNow;
        foreach (var table in plan.Tables.Where(table => table.View is not null))
        {
            var sql = CompileQueryPlan(table.View!, connection, [], limits, token, compilers, budget, statementTime);
            var compiled = compilers[^1]; var columns = table.Definition.SqlView!.Columns;
            if (compiled.Columns.Count != columns.Count || compiled.Columns.Where((column, index) => column.Type != columns[index].Type).Any())
                throw new CpfException("SQL0104", "View result shape changed; recreate the view and its dependents.");
            table.LogicalSql = "(SELECT " + string.Join(',', columns.Select((column, index) => "__v." + Quote("p" + index) + " AS " + Quote(column.Name))) + " FROM (" + sql + ") AS __v)";
        }
        var compiler = new SqlQueryCompiler(connection, plan.Query, plan.Tables, plan.Options, parameters, limits, token, budget, statementTime); compilers.Add(compiler);
        return compiler.Compile();
    }
}
