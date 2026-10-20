using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record AlterDependentPlan(IReadOnlyDictionary<QualifiedName, FileDefinition> Updated, IReadOnlySet<QualifiedName> Deleted);
    private AlterDependentPlan PlanAlterDependents(SqliteConnection connection, SqliteTransaction transaction, BoundTableAlteration bound,
        IReadOnlyList<QualifiedName> dependents, CancellationToken token)
    {
        var deleted = new HashSet<QualifiedName>(); var updated = new Dictionary<QualifiedName, FileDefinition>();
        var definitions = new Dictionary<QualifiedName, FileDefinition> { [bound.Key] = bound.Definition };
        foreach (var key in dependents.Reverse())
        {
            token.ThrowIfCancellationRequested();
            var definition = ReadDefinition(connection, transaction, key.Library, key.Name.Value) ?? throw new CpfException("SQL0204", "Dependent file disappeared.");
            if (definition.SqlView is { } view)
            {
                if (view.Sources.Any(source => deleted.Contains(new(source.Library, source.Name)))) { Drop(); continue; }
                var droppedReferences = AlterViewDroppedColumns(view, bound).ToArray();
                if (droppedReferences.Length != 0)
                {
                    if (droppedReferences.Any(column => !bound.Dropped[column])) throw new CpfException("SQL0478", "A view depends on a dropped column.");
                    Drop(); continue;
                }
                var compilers = new List<SqlQueryCompiler>();
                try
                {
                    var query = BoundViewQuery(view); var plan = ResolveQueryPlan(connection, transaction, query, view.Options, token, (_, _, _) => { }, [], definitions: definitions);
                    _ = CompileQueryPlan(plan, connection, [], new(), token, compilers);
                    if (compilers[^1].Columns.Count != view.Columns.Count) throw new CpfException("SQL0104", "Dependent view changed column count.");
                    var columns = compilers[^1].Columns.Select((column, ordinal) => column with { Name = view.Columns[ordinal].Name, Calculated = false }).ToArray();
                    definition.SqlView = view with { Columns = columns };
                    definition.Formats = [ViewRecordFormat(definition.PrimaryFormat.Name, columns)];
                    updated[key] = definitions[key] = definition;
                }
                finally { foreach (var compiler in compilers.AsEnumerable().Reverse()) compiler.Dispose(); }
            }
            else if (definition.Logical is { } logical)
            {
                var source = definitions.GetValueOrDefault(new(logical.SourceLibrary, logical.SourceFile)) ?? ReadDefinition(connection, transaction, logical.SourceLibrary, logical.SourceFile) ?? throw new CpfException("SQL0204", "Logical source disappeared.");
                if (logical.SourceLibrary != bound.Key.Library || logical.SourceFile != bound.Key.Name.Value) throw new CpfException("SQL0104", "Unexpected ALTER TABLE logical dependency.");
                var shared = !definition.SqlIndex && definition.PrimaryFormat.Name == bound.Original.PrimaryFormat.Name;
                var referenced = definition.PrimaryFormat.Fields.Where(field => bound.Dropped.ContainsKey(field.Name)).ToArray();
                var ruleDropped = logical.Rules.SelectMany(rule => rule.Conditions).Any(condition => bound.Dropped.ContainsKey(condition.Field));
                if (referenced.Length != 0 || ruleDropped)
                {
                    if (!shared || ruleDropped || referenced.Any(field => field.Sequence != 0))
                    {
                        if (referenced.Any(field => !bound.Dropped[field.Name]) || logical.Rules.SelectMany(rule => rule.Conditions).Any(condition => bound.Dropped.TryGetValue(condition.Field, out var cascade) && !cascade))
                            throw new CpfException("SQL0478", "A logical file or index depends on a dropped column.");
                        Drop(); continue;
                    }
                }
                var previous = definition.PrimaryFormat.Fields;
                var selected = shared ? source.PrimaryFormat.Fields : previous.Where(field => source.PrimaryFormat.Find(field.Name) is not null);
                definition.PrimaryFormat.Fields = selected.Select(field => {
                    var next = CloneAlterField(source.PrimaryFormat.Find(field.Name)!); var old = previous.Find(item => item.Name == field.Name);
                    next.Sequence = old?.Sequence ?? 0; next.Descending = old?.Descending ?? false; return next;
                }).ToList();
                definition.PrimaryFormat.AssignPositions();
                if (definition.PrimaryFormat.RecordLength > 32766) throw new CpfException("SQL0904", "Dependent logical record is too wide.");
                using (var predicate = connection.CreateCommand())
                { predicate.Transaction = transaction; _ = LogicalPredicate(predicate, logical, source.PrimaryFormat); }
                updated[key] = definitions[key] = definition;
            }
            else throw new CpfException("SQL0104", "Unimplemented ALTER TABLE dependent object.");

            void Drop()
            {
                deleted.Add(key);
            }
        }
        return new(updated, deleted);
    }
    private void PublishAlterDependents(SqliteConnection connection, SqliteTransaction transaction, BoundTableAlteration bound,
        IReadOnlyList<QualifiedName> dependents, AlterDependentPlan plan)
    {
        foreach (var (key, definition) in plan.Updated)
        {
            if (definition.Logical is { } logical)
                definition.Logical = logical with { UniquePaths = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat,
                    bound.Definition.PrimaryFormat, logical.Members.Values.SelectMany(members => members).Distinct().ToArray()) };
            PublishAlterDefinition(connection, transaction, key, definition);
        }
        foreach (var key in dependents.Where(plan.Deleted.Contains)) ObjectCatalogOperations.DeleteCore(key, FileType, connection, transaction);
    }
    private static IEnumerable<string> AlterViewDroppedColumns(SqlViewDefinition view, BoundTableAlteration bound)
    {
        var query = BoundViewQuery(view); var sources = QuerySourceList(query.From).ToArray();
        var rootQualifiers = sources.Select((source, ordinal) => (source, reference: view.Sources[ordinal]))
            .Where(item => item.reference.Library == bound.Key.Library && item.reference.Name == bound.Key.Name.Value)
            .Select(item => item.source.Alias ?? item.source.Table.Name).ToHashSet();
        if (rootQualifiers.Count == 0) return [];
        var expressions = query.Columns.Select(column => column.Value).Concat(query.GroupBy);
        if (query.Where is { } where) expressions = expressions.Append(where);
        if (query.Having is { } having) expressions = expressions.Append(having);
        expressions = expressions.Concat(JoinExpressions(query.From));
        return expressions.SelectMany(Columns).Where(column => bound.Dropped.ContainsKey(column.Name) &&
            (column.Qualifier is null || rootQualifiers.Contains(column.Qualifier))).Select(column => column.Name).Distinct();
        static IEnumerable<SqlColumn> Columns(SqlExpression expression) => expression is SqlColumn column ? [column] : ViewChildren(expression).SelectMany(Columns);
        static IEnumerable<SqlExpression> JoinExpressions(SqlFrom source) => source is SqlJoinSource join ?
            JoinExpressions(join.Left).Concat(JoinExpressions(join.Right)).Concat(join.On is null ? [] : new[] { join.On }) : [];
    }
}
