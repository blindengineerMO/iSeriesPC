using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private void BindSqlForeignKeys(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key,
        FileDefinition definition, SqlExecutionOptions options, IReadOnlySet<string>? previousNames = null)
    {
        if (definition.SqlTable?.ForeignKeys is not { Count: > 0 } foreign) return;
        if (definition.MaximumMembers != 1 || JobTemporaryLibrary.LogicalLibrary(connection, transaction, key.Library) == "QTEMP")
            throw new CpfException("SQL7008", "Referential constraints require permanent files with MAXMBRS(1).");
        var used = SqlConstraintCatalog.Names(connection, transaction, key.Library, key);
        used.UnionWith(definition.SqlTable.Constraints.Select(item => item.Name).Concat((definition.SqlTable.Checks ?? []).Select(item => item.Name)).OfType<string>());
        var result = new List<SqlForeignConstraint>();
        foreach (var item in foreign)
        {
            string name;
            if (item.Name is { } specified) { name = specified; if (!used.Add(name)) throw new CpfException("SQL0601", "Constraint name already exists in schema."); }
            else do { name = "F" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(); } while (!used.Add(name));
            if (item.Columns.Count is < 1 or > 120 || item.Columns.Distinct().Count() != item.Columns.Count)
                throw new CpfException("SQL0538", "Invalid foreign key columns.");
            var fields = item.Columns.Select(column => definition.PrimaryFormat.Find(column) ?? throw new CpfException("SQL0206", "Unknown foreign key column " + column + ".")).ToArray();
            if (item.Defined && previousNames?.Contains(name) == true) { result.Add(item with { Name = name }); continue; }
            if (fields.Sum(field => field.StorageLength + (field.NullCapable ? 1 : 0)) > 32766) throw new CpfException("SQL0904", "Foreign key is too wide.");
            if (item.DeleteRule == SqlForeignDelete.SetNull && fields.All(field => !field.NullCapable))
                throw new CpfException("SQL0629", "SET NULL requires a nullable foreign key column.");
            var parent = ResolveParent(item); var self = parent == key;
            var parentDefinition = self ? definition : ReadDefinition(connection, transaction, parent.Library, parent.Name.Value)
                ?? throw new CpfException("SQL0204", "Parent table not found.");
            if (parentDefinition.SqlTable is null || parentDefinition.SqlView is not null || parentDefinition.Logical is not null ||
                parentDefinition.MaximumMembers != 1 || JobTemporaryLibrary.LogicalLibrary(connection, transaction, parent.Library) == "QTEMP")
                throw new CpfException("SQL7008", "The parent must be a permanent single-member SQL physical table.");
            var columns = item.ParentColumns ?? parentDefinition.SqlTable.Constraints.FirstOrDefault(candidate => candidate.Primary)?.Columns
                ?? throw new CpfException("SQL0539", "The parent has no primary key.");
            foreach (var column in columns)
                if (parentDefinition.PrimaryFormat.Find(column) is null) throw new CpfException("SQL0206", "Unknown parent key column " + column + ".");
            if (columns.Count != fields.Length) throw new CpfException("SQL0538", "Foreign and parent keys must have the same number of columns.");
            if (columns.Distinct().Count() != columns.Count ||
                !parentDefinition.SqlTable.Constraints.Any(candidate => candidate.Columns.Count == columns.Count && candidate.Columns.ToHashSet().SetEquals(columns)))
                throw new CpfException("SQL0539", "The parent columns must identify a primary or unique constraint.");
            for (var ordinal = 0; ordinal < fields.Length; ordinal++)
            {
                var parentField = parentDefinition.PrimaryFormat.Find(columns[ordinal]) ?? throw new CpfException("SQL0206", "Unknown parent key column.");
                var field = fields[ordinal];
                if (!SqlForeignSchema.SameType(field,parentField))
                    throw new CpfException("SQL0538", "Foreign and parent key column attributes must match.");
            }
            if (!self)
            {
                RequireUnenlisted(parent.Library, parent.Name.Value, "Referential DDL");
                if (previousNames?.Contains(name) != true)
                    new ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, parent.Library, parent.Name.Value,
                        AuthorityBit.None, alternatives: AuthorityBit.ObjectReference | AuthorityBit.ObjectManagement);
            }
            var parentMember = self ? item.ParentMember ?? key.Name.Value : SingleMember(parent);
            result.Add(item with { Name = name, Parent = new(parent.Library, parent.Name.Value), ParentColumns = columns, ParentMember = parentMember });
        }
        definition.SqlTable = definition.SqlTable with { ForeignKeys = result };
        return;
        QualifiedName ResolveParent(SqlForeignConstraint item)
        {
            if (item.ParentMember is not null) return new(item.Parent.Schema!, item.Parent.Name);
            var schema = item.Parent.Schema ?? options.DefaultCollection;
            if (schema is null && options.Naming == SqlNaming.Sql) schema = options.DefaultSchema;
            if (schema is null)
            {
                foreach (var library in options.LibraryList)
                {
                    var candidate = new QualifiedName(ResolveLibrary(library), item.Parent.Name);
                    if (candidate == key || ReadDefinition(connection, transaction, candidate.Library, candidate.Name.Value) is not null) return candidate;
                }
                schema = options.CurrentLibrary;
            }
            return new(ResolveLibrary(schema), item.Parent.Name);
        }
        string SingleMember(QualifiedName parent)
        {
            using var read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr";
            read.Parameters.AddWithValue("$lib", parent.Library); read.Parameters.AddWithValue("$name", parent.Name.Value);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new CpfException("SQL0204", "Parent member not found.");
            var member = reader.GetString(0); if (reader.Read()) throw new CpfException("SQL7008", "Parent has more than one member.");
            return member;
        }
    }
    private static void CheckSqlForeignRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader(); if (reader.Read()) throw new CpfException("SQL0530", "A nonnull foreign key has no matching parent key.");
    }
    private void EnsureForeignParents(SqliteConnection connection, SqliteTransaction transaction, QualifiedName target,
        FileDefinition definition, CancellationToken token)
    {
        foreach (var foreign in (definition.SqlTable?.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced).DistinctBy(item => (item.Parent.Schema, item.Parent.Name)))
        {
            var key = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name); if (key == target) continue;
            var parent = ReadDefinition(connection, transaction, key.Library, key.Name.Value)!; var table = SqlForeignSchema.ParentTable(foreign);
            using var schema = connection.CreateCommand(); schema.Transaction = transaction;
            schema.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table"; schema.Parameters.AddWithValue("$table", table);
            var actual = schema.ExecuteScalar() as string ?? throw new CpfException("SQL0204", "Parent member not found.");
            var expected = CreateMemberTableSql(parent.PrimaryFormat, table, parent.SqlTable);
            if (actual[actual.IndexOf('(')..] == expected[expected.IndexOf('(')..]) continue;
            // Upgrade the previous bridge representation in the same writer transaction.
            // This changes internal collations/indexes, never the external record layout.
            new JobLockStore(_factory).RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
            var bound = new BoundTableAlteration(key, parent, parent, [foreign.ParentMember!],
                new Dictionary<string,string> { [foreign.ParentMember!] = RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, foreign.ParentMember!)! },
                new Dictionary<string,bool>(), new Dictionary<string,SqlAddColumn>());
            var indexes = new List<(string Table,string Sql)>(); var triggers = new List<(string Table,string Sql)>();
            CaptureAlterSchema(connection, transaction, bound, table, indexes, triggers);
            RewriteAlterMember(connection, transaction, bound, foreign.ParentMember!, DateTimeOffset.UtcNow, token);
            foreach (var (_, sql) in indexes.Concat(triggers)) RunAlterSql(connection, transaction, sql, token);
        }
    }
    private sealed class ForeignDdlScope : IDisposable
    {
        private readonly SqliteConnection _connection;
        internal ForeignDdlScope(SqliteConnection connection)
        { _connection = connection; Configure(0); }
        public void Dispose()
        { Configure(1); }
        private void Configure(int value)
        {
            var status = SQLitePCL.raw.sqlite3_db_config(_connection.Handle!, SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_FKEY, value, out var actual);
            SqliteException.ThrowExceptionForRC(status, _connection.Handle);
            if (actual != value) throw new CpfException("SQL0901", "Unable to configure atomic referential DDL.");
        }
    }
}
