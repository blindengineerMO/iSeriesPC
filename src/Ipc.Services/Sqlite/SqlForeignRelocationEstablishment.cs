using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Ipc.Services.Sqlite;

// The outer catalog operation owns the writer and this scope's leases. Related
// changes are delegated constraint work, never additional caller data authority.
internal sealed class SqlForeignRelocationEstablishment(SqliteConnectionFactory factory, CancellationToken token) : IDisposable
{
    private sealed record Snapshot(FileDefinition Definition, string Owner, Dictionary<string, string?> Members);
    private readonly Dictionary<QualifiedName, Snapshot> _expected = [];
    private readonly HashSet<QualifiedName> _roots = [];
    private readonly HashSet<QualifiedName> _allocated = [];
    private readonly List<IDisposable> _leases = [];
    private QualifiedName? _copyTarget;
    private Snapshot? _copySnapshot;
    internal string? CopyDefinition { get; private set; }

    internal void Prepare(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName source, QualifiedName target, string type, bool copy, string? newOwner, bool copyData = true, bool copyConstraints = true)
    {
        if (type is not (ObjectType.File or ObjectType.Library)) return;
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        var mapping = files.Keys.Where(key => type == ObjectType.Library ? key.Library == source.Name.Value : key == source)
            .ToDictionary(key => key, key => type == ObjectType.Library ? new QualifiedName(target.Name.Value, key.Name.Value) : target);
        _roots.UnionWith(mapping.Values);
        if (type == ObjectType.File) _roots.Add(target);
        if (copy && type == ObjectType.File)
        {
            CopyDefinition = SqlConstraintCatalog.PrepareRelocation(connection, transaction, source, target, true, copyConstraints, copyData, token);
            if (files.TryGetValue(source, out var original))
            {
                var next = Named(CopyDefinition is null ? original : FileDefinition.FromJson(CopyDefinition), target.Name.Value);
                var old = Capture(source);
                _copyTarget = target;
                _copySnapshot = new(next, newOwner ?? old.Owner, old.Members.ToDictionary(item => item.Key, _ => (string?)null));
            }
        }
        var affected = new HashSet<QualifiedName>();
        foreach (var foreign in _copySnapshot?.Definition.SqlTable?.ForeignKeys ?? [])
        {
            var parent = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
            if (files.ContainsKey(parent)) { Capture(parent); affected.Add(parent); }
        }
        foreach (var (key, definition) in files)
        {
            var foreign = definition.SqlTable?.ForeignKeys ?? [];
            if (mapping.ContainsKey(key) && !copy || foreign.Any(item =>
                    !copy && mapping.ContainsKey(new(item.Parent.Schema!, item.Parent.Name)) ||
                    item.Defined && (_roots.Contains(key) || _roots.Contains(new(item.Parent.Schema!, item.Parent.Name)))))
                affected.Add(key);
        }
        foreach (var key in affected.ToArray())
        {
            Capture(key);
            foreach (var foreign in files[key].SqlTable?.ForeignKeys ?? [])
            {
                var parent = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
                if (files.ContainsKey(parent)) { Capture(parent); affected.Add(parent); }
            }
        }
        // Query opens through an index/logical file must conflict before ALTER's
        // parser probe can encounter a retained SQLite schema reader.
        foreach (var key in affected.ToArray())
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT lib,name FROM sys_file_defs WHERE json_extract(def,'$.logical.sourceLibrary')=$lib AND json_extract(def,'$.logical.sourceFile')=$name";
            command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
            var logicals = new List<QualifiedName>(); using (var reader = command.ExecuteReader())
                while (reader.Read()) logicals.Add(new(reader.GetString(0), reader.GetString(1)));
            foreach (var logical in logicals) { Capture(logical); affected.Add(logical); }
        }
        var locks = new JobLockStore(factory);
        foreach (var key in affected)
        {
            RequireAvailable(key, locks);
            if (OperationIdentity.Current?.Job is { } job)
            {
                _leases.Add(locks.AcquireReferentialFile(job, key, token,
                    () => ValidatePublisher(connection, transaction), transaction, JobLockMode.Exclusive));
                _allocated.Add(key);
            }
        }
        if (!copy)
        {
            var projected = _expected.ToArray(); _expected.Clear();
            foreach (var (key, snapshot) in projected)
            {
                var afterKey = mapping.GetValueOrDefault(key, key);
                // Logical files need the same physical identity projection even
                // though their own names may be outside the moved library.
                if (type == ObjectType.Library && key.Library == source.Name.Value) afterKey = new(target.Name.Value, key.Name.Value);
                var next = Named(snapshot.Definition, afterKey.Name.Value);
                if (next.SqlTable is { } sql) next.SqlTable = sql with { ForeignKeys = sql.ForeignKeys?.Select(item =>
                    mapping.TryGetValue(new(item.Parent.Schema!, item.Parent.Name), out var parent)
                        ? item with { Parent = new(parent.Library, parent.Name.Value) } : item).ToArray() };
                if (next.Logical is { } logical && mapping.TryGetValue(new(logical.SourceLibrary, logical.SourceFile), out var physical))
                    next.Logical = logical with { SourceLibrary = physical.Library, SourceFile = physical.Name.Value };
                _expected.Add(afterKey, snapshot with { Definition = next });
            }
        }
        return;

        Snapshot Capture(QualifiedName key)
        {
            if (_expected.TryGetValue(key, out var captured)) return captured;
            var definition = ReadDefinition(connection, transaction, key) ?? throw Invalid();
            captured = new(definition, ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, ObjectType.File).Owner,
                Members(connection, transaction, key));
            _expected.Add(key, captured); return captured;
        }
    }

    internal void Complete(SqliteConnection connection, SqliteTransaction transaction)
    {
        ValidateSnapshots(connection, transaction);
        if (_copyTarget is { } target && _copySnapshot is { } copied)
        {
            var members = Members(connection, transaction, target);
            if (!members.Keys.ToHashSet().SetEquals(copied.Members.Keys) || members.Values.Any(id => id is null)) throw Invalid();
            _expected.Add(target, copied with { Members = members });
            ValidateSnapshots(connection, transaction);
        }
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        var plans = new List<(QualifiedName Key, FileDefinition Before, FileDefinition After)>();
        foreach (var (key, original) in files)
        {
            var candidates = (original.SqlTable?.ForeignKeys ?? []).Where(item => item.Defined &&
                (_roots.Contains(key) || _roots.Contains(new(item.Parent.Schema!, item.Parent.Name)))).ToArray();
            if (candidates.Length == 0 || original.MaximumMembers != 1) continue;
            var members = Members(connection, transaction, key); if (members.Count != 1) continue;
            var next = FileDefinition.FromJson(original.ToJson());
            foreach (var foreign in candidates)
            {
                token.ThrowIfCancellationRequested();
                var parentKey = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
                if (!files.TryGetValue(parentKey, out var parent) || parent.SqlTable is null || parent.Logical is not null || parent.SqlView is not null) continue;
                var parentMembers = Members(connection, transaction, parentKey); if (parentMembers.Count != 1) continue;
                var bound = foreign with { Defined = false, ParentMember = parentMembers.Keys.Single(), CheckPending = false,
                    ParentColumns = foreign.ParentColumns ?? parent.SqlTable.Constraints.FirstOrDefault(item => item.Primary)?.Columns };
                try { SqlForeignCatalog.ValidateReference(original, bound, parent); }
                catch (CpfException error) when (error.MessageId is "SQL0538" or "SQL0539" or "SQL7008") { continue; }
                bound = bound with { CheckPending = bound.Disabled || SqlForeignCatalog.HasViolations(connection, transaction,
                    key.Library + "." + key.Name.Value + "." + members.Keys.Single(), original, bound, token) };
                next.SqlTable = next.SqlTable! with { ForeignKeys = next.SqlTable!.ForeignKeys!.Select(item => item.Name == bound.Name ? bound : item).ToArray() };
            }
            if (original.ToJson() != next.ToJson()) plans.Add((key, original, next));
        }
        var locks = new JobLockStore(factory);
        foreach (var key in _expected.Keys)
        {
            if (!_allocated.Contains(key))
            {
                RequireAvailable(key, locks);
                if (OperationIdentity.Current?.Job is { } job)
                {
                    _leases.Add(locks.AcquireReferentialFile(job, key, token,
                        () => ValidatePublisher(connection, transaction), transaction, JobLockMode.Exclusive));
                    _allocated.Add(key);
                }
            }
        }
        if (plans.Count != 0)
        {
            SqlForeignCatalog.NullHooks(connection, transaction, install: false);
            foreach (var (key, before, after) in plans)
            {
                if (!_expected.TryGetValue(key, out var snapshot)) throw Invalid();
                _expected[key] = snapshot with { Definition = after };
                foreach (var member in snapshot.Members.Keys)
                    SqlForeignMemberRewrite.Apply(connection, transaction, key, member, before, after, token);
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*FILE'";
                update.Parameters.AddWithValue("$lib", key.Library); update.Parameters.AddWithValue("$name", key.Name.Value);
                update.Parameters.AddWithValue("$def", after.ToJson()); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.ExecuteNonQuery();
            }
            SqlForeignCatalog.NullHooks(connection, transaction, install: true);
        }
        ValidateSnapshots(connection, transaction);
        foreach (var (key, expected) in _expected)
        {
            if (expected.Definition.SqlTable is null || expected.Definition.Logical is not null || expected.Definition.SqlView is not null) continue;
            foreach (var member in expected.Members.Keys)
            {
                var table = key.Library + "." + key.Name.Value + "." + member;
                foreach (var foreign in (expected.Definition.SqlTable.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced))
                    if (SqlForeignCatalog.HasViolations(connection, transaction, table, expected.Definition, foreign, token))
                        throw new CpfException("SQL0530", "Related data changed during referential establishment.", sqlState: "23503");
                using var compile = connection.CreateCommand(); compile.Transaction = transaction;
                foreach (var sql in new[] { "INSERT INTO " + SqlForeignSchema.Quote(table) + " DEFAULT VALUES",
                    "DELETE FROM " + SqlForeignSchema.Quote(table) + " WHERE 0",
                    "UPDATE " + SqlForeignSchema.Quote(table) + " SET " + string.Join(',', expected.Definition.PrimaryFormat.Fields.Select(field => SqlForeignSchema.Quote(field.Name) + "=" + SqlForeignSchema.Quote(field.Name))) + " WHERE 0" })
                { compile.CommandText = "EXPLAIN " + sql; SqliteCancellation.Run(connection, token, compile.ExecuteNonQuery); }
            }
        }
        ValidatePublisher(connection, transaction);
    }

    private void RequireAvailable(QualifiedName key, JobLockStore locks)
    {
        if (factory.RecordTransactions.IsEnlisted(key)) throw JobRecordTransactions.Invalid("Referential relocation requires ending the related record unit first.");
        locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, ObjectType.File));
    }

    private void ValidateSnapshots(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var (key, snapshot) in _expected)
        {
            var members = Members(connection, transaction, key);
            if (ReadDefinition(connection, transaction, key)?.ToJson() != snapshot.Definition.ToJson() ||
                ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, ObjectType.File).Owner != snapshot.Owner ||
                members.Count != snapshot.Members.Count || snapshot.Members.Any(item => !members.TryGetValue(item.Key, out var id) || id != item.Value)) throw Invalid();
            using var allocation = factory.Locks.Open(); using var held = allocation.CreateCommand();
            held.CommandText = "SELECT count(*) FROM holders WHERE library=$lib AND name=$name AND type='*FILE' AND lifetime NOT IN('Command','Referential')";
            held.Parameters.AddWithValue("$lib", key.Library); held.Parameters.AddWithValue("$name", key.Name.Value);
            if (Convert.ToInt64(held.ExecuteScalar()) != 0) throw new CpfException("CPF1002", "A related file was allocated during referential relocation.");
        }
    }

    private void ValidatePublisher(SqliteConnection connection, SqliteTransaction transaction)
    {
        token.ThrowIfCancellationRequested();
        if (OperationIdentity.Current is not { } identity) return;
        new ServiceAuthorization(factory).RequireCaller(connection, transaction, identity.Principal);
        if (identity.Job is not { } job) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
        JobRecordTransactions.BindJob(command, job); command.Parameters.AddWithValue("$owner", identity.Principal);
        var state = command.ExecuteScalar(); if (state is null) throw new CpfException("CPF1241", "Relocation job is no longer active.");
        if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("Relocation job was canceled.");
    }

    private static FileDefinition? ReadDefinition(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        return command.ExecuteScalar() is string json ? FileDefinition.FromJson(json) : null;
    }
    private static Dictionary<string, string?> Members(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT m.mbr,s.id FROM sys_file_members m LEFT JOIN sys_record_slots s USING(lib,name,type,mbr) WHERE m.lib=$lib AND m.name=$name AND m.type='*FILE' ORDER BY m.created,m.mbr";
        command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        var result = new Dictionary<string, string?>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)); return result;
    }
    private static CpfException Invalid() => new("CPF32B2", "Related definitions, ownership or member identities changed during referential relocation.");
    private static FileDefinition Named(FileDefinition definition, string name)
    {
        var json = JsonNode.Parse(definition.ToJson())!; json["name"] = name;
        return FileDefinition.FromJson(json.ToJsonString());
    }
    public void Dispose() { for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose(); }
}
