using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private static bool SameForeignType(FieldSpec left, FieldSpec right) => SqlForeignSchema.SameType(left,right);
    private static bool HasParentKey(FileDefinition parent, SqlForeignConstraint foreign) => parent.SqlTable!.Constraints.Any(key =>
        key.Columns.Count == foreign.ParentColumns!.Count && key.Columns.ToHashSet().SetEquals(foreign.ParentColumns));
    private static bool ForeignDropAllowed(FileDefinition original, SqlForeignConstraint foreign, IReadOnlyList<SqlTableChange> changes, IReadOnlyDictionary<string,bool> dropped)
    {
        var columns = foreign.ParentColumns!;
        if (columns.Any(dropped.ContainsKey))
            return columns.All(dropped.ContainsKey) || columns.Where(dropped.ContainsKey).All(column => dropped[column]);
        var old = original.SqlTable!.Constraints.Single(key => key.Columns.Count == columns.Count && key.Columns.ToHashSet().SetEquals(columns));
        return changes.OfType<SqlDropKey>().Any(drop => (drop.Primary ? old.Primary : drop.Name == old.Name) && drop.Cascade);
    }
    private static void CascadeSelfForeignKeys(QualifiedName key, FileDefinition original, FileDefinition proposed,
        IReadOnlyList<SqlTableChange> changes, IReadOnlyDictionary<string,bool> dropped)
    {
        var foreign = (proposed.SqlTable!.ForeignKeys ?? []).ToList();
        foreach (var item in foreign.ToArray())
        {
            if (item.Defined || item.ParentMember is null || item.Parent.Schema != key.Library || item.Parent.Name != key.Name.Value || HasParentKey(proposed, item)) continue;
            if (!ForeignDropAllowed(original, item, changes, dropped)) throw new CpfException("SQL0478", "A referential constraint depends on the parent key.");
            foreign.Remove(item);
        }
        proposed.SqlTable = proposed.SqlTable with { ForeignKeys = foreign.Count == 0 ? null : foreign };
    }
    private IReadOnlyList<BoundTableAlteration> PlanIncomingForeignChanges(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName key, FileDefinition original, FileDefinition proposed, IReadOnlyList<SqlTableChange> changes,
        IReadOnlyDictionary<string,bool> dropped, bool deleting = false)
    {
        var result = new List<BoundTableAlteration>();
        foreach (var file in SqlForeignCatalog.Files(connection, transaction).Where(file => file.Key != key))
        {
            var foreign = (file.Definition.SqlTable!.ForeignKeys ?? []).ToList(); var removed = false;
            foreach (var item in foreign.ToArray().Where(item => item.Parent.Schema == key.Library && item.Parent.Name == key.Name.Value && (deleting || !item.Defined)))
            {
                if (deleting || !HasParentKey(proposed, item))
                {
                    if (!deleting && !ForeignDropAllowed(original, item, changes, dropped)) throw new CpfException("SQL0478", "A referential constraint depends on the parent key.");
                    foreign.Remove(item); removed = true; continue;
                }
                for (var ordinal = 0; ordinal < item.Columns.Count; ordinal++)
                    if (!SameForeignType(file.Definition.PrimaryFormat.Find(item.Columns[ordinal])!, proposed.PrimaryFormat.Find(item.ParentColumns![ordinal])!))
                        throw new CpfException("SQL0190", "Changing referenced column attributes requires native check-pending state transitions.");
            }
            if (!removed) continue;
            RequireUnenlisted(file.Key.Library, file.Key.Name.Value, "Cascaded referential DDL");
            var next = FileDefinition.FromJson(file.Definition.ToJson()); next.SqlTable = next.SqlTable! with { ForeignKeys = foreign.Count == 0 ? null : foreign };
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT m.mbr,s.id FROM sys_file_members m JOIN sys_record_slots s USING(lib,name,type,mbr) WHERE m.lib=$lib AND m.name=$name AND m.type='*FILE' ORDER BY m.created,m.mbr";
            read.Parameters.AddWithValue("$lib", file.Key.Library); read.Parameters.AddWithValue("$name", file.Key.Name.Value);
            var members = new List<string>(); var identities = new Dictionary<string,string>();
            using (var reader = read.ExecuteReader()) while (reader.Read()) { members.Add(reader.GetString(0)); identities.Add(reader.GetString(0), reader.GetString(1)); }
            result.Add(new(file.Key,file.Definition,next,members,identities,new Dictionary<string,bool>(),new Dictionary<string,SqlAddColumn>()));
        }
        return result;
    }
    private void ApplyIncomingForeignChanges(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<BoundTableAlteration> changes, CancellationToken token, bool allocated = false, bool trustExistingChecks = false)
    {
        foreach (var bound in changes)
        {
            if (!allocated)
            {
                var locks = new JobLockStore(_factory); locks.RequireNoPersistentAllocation(new(bound.Key.Library,bound.Key.Name.Value,FileType));
                foreach (var logical in SqlTableLogicalDependents(connection,transaction,bound.Key.Library,bound.Key.Name.Value))
                    locks.RequireNoPersistentAllocation(new(logical.Library,logical.Name.Value,FileType));
            }
            var triggers = new List<(string Table,string Sql)>(); var indexes = new List<(string Table,string Sql)>();
            foreach (var member in bound.Members)
            {
                var table = MemberTable(bound.Key.Library,bound.Key.Name.Value,member);
                CaptureAlterSchema(connection,transaction,bound,table,new List<(string,string)>(),triggers);
                using var read = connection.CreateCommand(); read.Transaction = transaction;
                read.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='index' AND tbl_name=$table"; read.Parameters.AddWithValue("$table",table);
                using var reader = read.ExecuteReader(); while (reader.Read())
                {
                    var name = reader.GetString(0);
                    if (!name.Contains("ipc_sql_unique_",StringComparison.Ordinal) && !name.Contains("ipc_sql_reference_",StringComparison.Ordinal) && !name.Contains("ipc_sql_foreign_",StringComparison.Ordinal)) indexes.Add((table,reader.GetString(1)));
                }
            }
            PublishAlterDefinition(connection,transaction,bound.Key,bound.Definition);
            foreach (var member in bound.Members) RewriteAlterMember(connection,transaction,bound,member,DateTimeOffset.UtcNow,token,trustExistingChecks);
            foreach (var (_,sql) in indexes.Concat(triggers)) RunAlterSql(connection,transaction,sql,token);
        }
    }
}
