using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static partial class SqlForeignCatalog
{
    internal static void CopyBindings(SqliteConnection connection, SqliteTransaction transaction, QualifiedName source, QualifiedName target, FileDefinition definition)
    {
        if (definition.SqlTable?.ForeignKeys is not { Count: > 0 } foreign) return;
        var files = Files(connection,transaction).ToDictionary(file => file.Key,file => file.Definition);
        var result = new List<SqlForeignConstraint>();
        foreach (var item in foreign)
        {
            var self = item.Parent.Schema == source.Library && item.Parent.Name == source.Name.Value;
            var parent = self ? target : new QualifiedName(item.Parent.Schema == source.Library ? target.Library : item.Parent.Schema!,item.Parent.Name);
            if (self) { result.Add(item with { Parent = new(parent.Library,parent.Name.Value) }); continue; }
            var rebound = item with { Parent = new(parent.Library, parent.Name.Value) };
            string? member = null;
            if (files.TryGetValue(parent, out var parentDefinition))
                try { ValidateReference(definition, item, parentDefinition); member = Member(connection, transaction, parent); }
                catch (CpfException error) when (error.MessageId is "SQL0204" or "SQL0538" or "SQL0539" or "SQL7008") { }
            if (member is null) { result.Add(rebound with { Defined = true, ParentMember = null, CheckPending = false }); continue; }
            rebound = rebound with { Defined = false, ParentMember = member };
            if (rebound.Disabled) rebound = rebound with { CheckPending = true };
            if (!rebound.Disabled)
                rebound = rebound with { CheckPending = HasViolations(connection, transaction, source.Library + "." + source.Name.Value + "." + Member(connection, transaction, source), definition, rebound, CancellationToken.None) };
            result.Add(rebound);
        }
        definition.SqlTable = definition.SqlTable with { ForeignKeys = result };
    }
    internal static void RelocateReferences(SqliteConnection connection, SqliteTransaction transaction, QualifiedName source, QualifiedName target)
    {
        foreach (var file in Files(connection,transaction))
        {
            var foreign = file.Definition.SqlTable?.ForeignKeys;
            if (foreign is null || !foreign.Any(item => item.Parent.Schema == source.Library && item.Parent.Name == source.Name.Value)) continue;
            file.Definition.SqlTable = file.Definition.SqlTable! with { ForeignKeys = foreign.Select(item =>
                item.Parent.Schema == source.Library && item.Parent.Name == source.Name.Value ? item with { Parent = new(target.Library,target.Name.Value) } : item).ToArray() };
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$lib",file.Key.Library); command.Parameters.AddWithValue("$name",file.Key.Name.Value);
            command.Parameters.AddWithValue("$def",file.Definition.ToJson()); command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
    }
    internal static void ValidateBindings(SqliteConnection connection, SqliteTransaction transaction, bool checkRows = true)
    {
        SqlConstraintCatalog.ValidateLimits(connection, transaction);
        var files = Files(connection,transaction).ToDictionary(file => file.Key,file => file.Definition);
        foreach (var (key,definition) in files)
        {
            if (definition.SqlTable?.ForeignKeys is not { Count: > 0 } foreign) continue;
            var member = foreign.All(item => item.Defined) ? OptionalMember(connection, transaction, key) : Member(connection,transaction,key);
            foreach (var item in foreign)
            {
                if (item.Defined) continue;
                var parent = new QualifiedName(item.Parent.Schema!,item.Parent.Name);
                if (!files.TryGetValue(parent,out var parentDefinition)) throw new CpfException("SQL0204","Constraint parent metadata is unavailable.");
                ValidateReference(definition,item,parentDefinition);
                if (Member(connection,transaction,parent) != item.ParentMember) throw new CpfException("SQL0104","Constraint parent member changed during publication.");
            }
            if (member is null) continue;
            var table = key.Library + "." + key.Name.Value + "." + member;
            using var schema = connection.CreateCommand(); schema.Transaction = transaction;
            schema.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table"; schema.Parameters.AddWithValue("$table",table);
            var sql = schema.ExecuteScalar() as string ?? throw new CpfException("SQL0204","Constraint member schema is unavailable.");
            if (!IsManagedSchema(connection,transaction,"table",table,table,sql)) throw new CpfException("SQL0104","Constraint metadata and physical schema disagree.");
        }
        if (!checkRows) return;
        using var check = connection.CreateCommand(); check.Transaction = transaction; check.CommandText = "PRAGMA foreign_key_check";
        using var reader = check.ExecuteReader(); if (reader.Read()) throw new CpfException("SQL0530","Relocation would leave an unmatched foreign key.",sqlState:"23503");
    }
    private static string Member(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file)
        => OptionalMember(connection, transaction, file) ?? throw new CpfException("SQL0204", "Constraint file has no member.");

    private static string? OptionalMember(SqliteConnection connection, SqliteTransaction transaction, QualifiedName file)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr";
        command.Parameters.AddWithValue("$lib",file.Library); command.Parameters.AddWithValue("$name",file.Name.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var member = reader.GetString(0); if (reader.Read()) throw new CpfException("SQL7008","Referential constraints require MAXMBRS(1)."); return member;
    }
    internal static void ValidateReference(FileDefinition child, SqlForeignConstraint foreign, FileDefinition parent)
    {
        if (child.MaximumMembers != 1 || parent.MaximumMembers != 1 || parent.SqlTable is null || parent.SqlView is not null || parent.Logical is not null)
            throw new CpfException("SQL7008","Referential constraints require single-member physical tables.");
        if (foreign.ParentColumns is null || !parent.SqlTable.Constraints.Any(item => item.Columns.Count == foreign.ParentColumns.Count && item.Columns.ToHashSet().SetEquals(foreign.ParentColumns)))
            throw new CpfException("SQL0539","The parent key does not identify a primary or unique constraint.");
        for (var index=0;index<foreign.Columns.Count;index++)
        {
            var left = child.PrimaryFormat.Find(foreign.Columns[index]); var right = parent.PrimaryFormat.Find(foreign.ParentColumns[index]);
            if (left is null || right is null || !SqlForeignSchema.SameType(left,right))
                throw new CpfException("SQL0538","Foreign and parent key attributes must match.");
        }
    }

    internal static bool HasViolations(SqliteConnection connection, SqliteTransaction transaction, string childTable,
        FileDefinition definition, SqlForeignConstraint foreign, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var nonnull = foreign.Columns.Select(column => "c." + SqlForeignSchema.Quote(column) + " IS NOT NULL");
        var matches = foreign.Columns.Select((column, index) => "p." + SqlForeignSchema.Quote(foreign.ParentColumns![index]) + "=c." + SqlForeignSchema.Quote(column) + " COLLATE " + SqlForeignSchema.Collation(definition.PrimaryFormat.Find(column)!));
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM " + SqlForeignSchema.Quote(childTable) + " c WHERE " + string.Join(" AND ", nonnull) +
            " AND NOT EXISTS(SELECT 1 FROM " + SqlForeignSchema.Quote(SqlForeignSchema.ParentTable(foreign)) + " p WHERE " + string.Join(" AND ", matches) + "))";
        return SqliteCancellation.Run(connection, token, () => Convert.ToInt32(command.ExecuteScalar()) != 0);
    }
}
