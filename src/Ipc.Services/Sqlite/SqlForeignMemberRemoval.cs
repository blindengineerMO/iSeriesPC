using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

// The catalog service also serves direct member removal, so this transition lives
// below the database facade. Its leases outlive the caller's writer transaction.
internal sealed class SqlForeignMemberRemoval(SqliteConnectionFactory factory) : IDisposable
{
    private sealed record Snapshot(string Definition, string Owner, Dictionary<string, string?> Members);
    private readonly Dictionary<QualifiedName, Snapshot> _expected = [];
    private readonly List<IDisposable> _allocations = [];
    private bool _changed;

    internal void Prepare(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root)
    {
        var token = factory.ShutdownToken;
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        var plans = new List<(QualifiedName Key, FileDefinition Original, FileDefinition Definition)>();
        Capture(root);
        foreach (var (key, original) in files)
        {
            token.ThrowIfCancellationRequested();
            var next = FileDefinition.FromJson(original.ToJson());
            next.SqlTable = next.SqlTable! with { ForeignKeys = next.SqlTable!.ForeignKeys?.Select(item =>
                key == root || item.Parent.Schema == root.Library && item.Parent.Name == root.Name.Value ?
                    item with { Defined = true, ParentMember = null } : item).ToArray() };
            if (next.ToJson() == original.ToJson()) continue;
            plans.Add((key, original, next)); Capture(key);
            foreach (var foreign in (original.SqlTable!.ForeignKeys ?? []).Where(item => !item.Defined))
                Capture(new(foreign.Parent.Schema!, foreign.Parent.Name));
            if (key != root)
            {
                using var logical = connection.CreateCommand(); logical.Transaction = transaction;
                logical.CommandText = "SELECT lib,name FROM sys_file_defs WHERE json_extract(def,'$.logical.sourceLibrary')=$lib AND json_extract(def,'$.logical.sourceFile')=$name";
                logical.Parameters.AddWithValue("$lib", key.Library); logical.Parameters.AddWithValue("$name", key.Name.Value);
                var dependents = new List<QualifiedName>();
                using (var reader = logical.ExecuteReader()) while (reader.Read()) dependents.Add(new(reader.GetString(0), reader.GetString(1)));
                foreach (var dependent in dependents) Capture(dependent);
            }
        }
        var locks = new JobLockStore(factory);
        foreach (var key in _expected.Keys)
        {
            if (factory.RecordTransactions.IsEnlisted(key))
                throw JobRecordTransactions.Invalid("RMVM requires ending the related record unit first.");
            locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, ObjectType.File));
        }
        // A schema probe must follow allocation checks: retained readers otherwise
        // turn a native allocation conflict into a SQLite schema-lock failure.
        foreach (var key in _expected.Keys.OrderBy(key => key.Library, StringComparer.Ordinal).ThenBy(key => key.Name.Value, StringComparer.Ordinal))
            if (OperationIdentity.Current?.Job is { } job)
                _allocations.Add(locks.AcquireReferentialFile(job, key, token, () => ValidatePublisher(connection, transaction), transaction, JobLockMode.Exclusive));
        if (plans.Count == 0) return;
        _changed = true;
        SqlForeignCatalog.NullHooks(connection, transaction, install: false);
        foreach (var (key, original, next) in plans)
        {
            // Removing the dependent member removes its physical path directly.
            // Other dependents retain their path, records and record incarnations.
            if (key != root)
                foreach (var member in _expected[key].Members.Keys)
                    RewriteDependent(connection, transaction, key, member, original, next);
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*FILE'";
            update.Parameters.AddWithValue("$def", next.ToJson()); update.Parameters.AddWithValue("$lib", key.Library);
            update.Parameters.AddWithValue("$name", key.Name.Value); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.ExecuteNonQuery();
            _expected[key] = _expected[key] with { Definition = next.ToJson() };
        }

        void Capture(QualifiedName key)
        {
            if (_expected.ContainsKey(key)) return;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
            var definition = command.ExecuteScalar() as string ?? throw new CpfException("CPF9801", "Related file definition is unavailable.");
            _expected.Add(key, new(definition, ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, ObjectType.File).Owner, Members(connection, transaction, key)));
        }
    }

    internal void Complete(SqliteConnection connection, SqliteTransaction transaction, QualifiedName root, string removed)
    {
        _expected[root].Members.Remove(removed);
        if (_changed) SqlForeignCatalog.NullHooks(connection, transaction, install: true);
        new ServiceAuthorization(factory).RequireBoundObject(connection, transaction, root.Library, root.Name.Value, AuthorityBit.ObjectExist | AuthorityBit.ObjectOperate);
        ValidatePublisher(connection, transaction);
        foreach (var (key, expected) in _expected)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
            var actual = Members(connection, transaction, key);
            if (command.ExecuteScalar() as string != expected.Definition ||
                ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, ObjectType.File).Owner != expected.Owner ||
                actual.Count != expected.Members.Count || expected.Members.Any(item => !actual.TryGetValue(item.Key, out var identity) || identity != item.Value))
                throw new CpfException("CPF32B2", "Related definitions, ownership or member identities changed during RMVM.");
            var definition = FileDefinition.FromJson(expected.Definition);
            if (_changed && definition.SqlTable is not null && definition.Logical is null && definition.SqlView is null)
                foreach (var member in actual.Keys)
                {
                    var table = SqlForeignSchema.Quote(key.Library + "." + key.Name.Value + "." + member);
                    foreach (var sql in new[] { "INSERT INTO " + table + " DEFAULT VALUES", "DELETE FROM " + table + " WHERE 0",
                        "UPDATE " + table + " SET " + string.Join(',', definition.PrimaryFormat.Fields.Select(field => SqlForeignSchema.Quote(field.Name) + "=" + SqlForeignSchema.Quote(field.Name))) + " WHERE 0" })
                    { command.CommandText = "EXPLAIN " + sql; SqliteCancellation.Run(connection, factory.ShutdownToken, command.ExecuteNonQuery); }
                }
            using var allocation = factory.Locks.Open(); using var held = allocation.CreateCommand();
            held.CommandText = "SELECT count(*) FROM holders WHERE library=$lib AND name=$name AND type='*FILE' AND lifetime NOT IN('Command','Referential')";
            held.Parameters.AddWithValue("$lib", key.Library); held.Parameters.AddWithValue("$name", key.Name.Value);
            if (Convert.ToInt64(held.ExecuteScalar()) != 0) throw new CpfException("CPF1002", "A related file was allocated during RMVM publication.");
        }
        if (_changed) SqlForeignCatalog.ValidateBindings(connection, transaction, checkRows: false);
    }

    private static Dictionary<string, string?> Members(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT m.mbr,s.id FROM sys_file_members m LEFT JOIN sys_record_slots s USING(lib,name,type,mbr) WHERE m.lib=$lib AND m.name=$name AND m.type='*FILE' ORDER BY m.created,m.mbr";
        command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        var result = new Dictionary<string, string?>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        return result;
    }

    private void ValidatePublisher(SqliteConnection connection, SqliteTransaction transaction)
    {
        factory.ShutdownToken.ThrowIfCancellationRequested();
        if (OperationIdentity.Current is not { } identity) return;
        new ServiceAuthorization(factory).RequireCaller(connection, transaction, identity.Principal);
        if (identity.Job is not { } job) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
        JobRecordTransactions.BindJob(command, job); command.Parameters.AddWithValue("$owner", identity.Principal);
        var state = command.ExecuteScalar(); if (state is null) throw new CpfException("CPF1241", "RMVM job is no longer active.");
        if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("RMVM job was canceled.");
    }

    private void RewriteDependent(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, string member, FileDefinition original, FileDefinition next)
    {
        var table = key.Library + "." + key.Name.Value + "." + member;
        var clauses = original.SqlTable!.ForeignKeys!.Where(SqlForeignSchema.Enforced).Where(item =>
            next.SqlTable!.ForeignKeys!.Single(after => after.Name == item.Name).Defined).Select(item => SqlForeignSchema.Clause(original.PrimaryFormat, item)).ToArray();
        if (clauses.Length == 0) return;
        var schema = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name=$table AND type IN('index','trigger') AND sql IS NOT NULL ORDER BY type,name";
            read.Parameters.AddWithValue("$table", table); using var reader = read.ExecuteReader(); while (reader.Read()) schema.Add(reader.GetString(0));
        }
        using var foreign = new SqlForeignDdlScope(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA ignore_check_constraints"; var checks = Convert.ToInt64(command.ExecuteScalar());
        try
        {
            command.CommandText = "PRAGMA ignore_check_constraints=ON"; command.ExecuteNonQuery();
            var staging = "ipc_rmvm_" + Guid.NewGuid().ToString("N");
            SqliteCancellation.Run(connection, factory.ShutdownToken, () => {
                new MemberTableCopy(connection, transaction).Copy([(table, staging)], sql => {
                    foreach (var clause in clauses)
                    {
                        var offset = sql.IndexOf(clause, StringComparison.Ordinal);
                        if (offset < 0 || sql.IndexOf(clause, offset + clause.Length, StringComparison.Ordinal) >= 0)
                            throw new CpfException("CPF32B2", "Constraint schema and metadata disagree during RMVM.");
                        var comma = offset - 1; while (comma >= 0 && char.IsWhiteSpace(sql[comma])) comma--;
                        if (comma < 0 || sql[comma] != ',') throw new CpfException("CPF32B2", "Invalid referential clause separator.");
                        sql = sql[..comma] + sql[(offset + clause.Length)..];
                    }
                    return sql;
                }, copySchemaObjects: false);
                command.CommandText = "DROP TABLE " + SqlForeignSchema.Quote(table); command.ExecuteNonQuery();
                command.CommandText = "ALTER TABLE " + SqlForeignSchema.Quote(staging) + " RENAME TO " + SqlForeignSchema.Quote(table); command.ExecuteNonQuery();
                foreach (var sql in schema) { command.CommandText = sql; command.ExecuteNonQuery(); }
                return 0;
            });
        }
        finally { command.CommandText = "PRAGMA ignore_check_constraints=" + checks; command.ExecuteNonQuery(); }
    }

    public void Dispose()
    { for (var index = _allocations.Count - 1; index >= 0; index--) _allocations[index].Dispose(); }
}

internal sealed class SqlForeignDdlScope : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _original;
    internal SqlForeignDdlScope(SqliteConnection connection)
    {
        _connection = connection;
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys";
        _original = Convert.ToInt32(command.ExecuteScalar()); Configure(0);
    }
    public void Dispose() => Configure(_original);
    private void Configure(int enabled)
    {
        var status = SQLitePCL.raw.sqlite3_db_config(_connection.Handle!, SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_FKEY, enabled, out var actual);
        SqliteException.ThrowExceptionForRC(status, _connection.Handle);
        if (actual != enabled) throw new CpfException("SQL0901", "Unable to configure referential member DDL.");
    }
}
