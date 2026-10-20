using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed record SqlConstraintChange(string Name, bool Changed, bool? Defined, bool? Disabled, bool? CheckPending, CpfException? Error = null);

public sealed partial class SqliteFileStore
{
    public IReadOnlyList<SqlConstraintChange> ChangeConstraints(string library, string name, IReadOnlyList<string> constraints,
        bool? enabled = null, bool check = true, CancellationToken cancellationToken = default)
    {
        if (constraints.Count is < 1 or > 300 || constraints.Any(string.IsNullOrWhiteSpace) ||
            constraints.Count != 1 && constraints.Any(item => item.StartsWith('*')))
            throw new CpfException("IPC0003", "CST requires *ALL, *CHKPND or 1–300 constraint names.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken);
        var token = lifetime.Token; token.ThrowIfCancellationRequested();
        var key = new QualifiedName(ResolveLibrary(library), name.ToUpperInvariant());
        string[] names;
        using (var connection = _factory.Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            var definition = RequireConstraintFile(connection, transaction, key);
            var candidates = (definition.SqlTable?.Checks ?? []).Select(item => (item.Name!, item.CheckPending))
                .Concat((definition.SqlTable?.ForeignKeys ?? []).Select(item => (item.Name!, item.CheckPending)));
            names = constraints[0].ToUpperInvariant() switch {
                "*ALL" => candidates.Select(item => item.Item1).ToArray(),
                "*CHKPND" => candidates.Where(item => item.CheckPending).Select(item => item.Item1).ToArray(),
                _ when constraints.Any(item => item.StartsWith('*')) => throw new CpfException("IPC0003", "Invalid CST selection."),
                _ => constraints.ToArray() };
        }
        // Native multi-constraint administration commits each constraint separately.
        // A diagnostic for one name must not undo a preceding successful change.
        var result = new List<SqlConstraintChange>();
        foreach (var constraint in names)
        {
            token.ThrowIfCancellationRequested();
            try { result.Add(ChangeConstraint(key, constraint, enabled, check, token)); }
            catch (CpfException error) { result.Add(new(constraint, false, null, null, null, error)); }
            catch (SqliteException error) { result.Add(new(constraint, false, null, null, null, SqlCheckRuntime.DecodeError(error) ?? new CpfException("CPF32B2", "Constraint state change failed: " + error.Message))); }
        }
        return result;
    }

    private FileDefinition RequireConstraintFile(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        new ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, key.Library, key.Name.Value,
            AuthorityBit.None, alternatives: AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter);
        var definition = ReadDefinition(connection, transaction, key.Library, key.Name.Value) ?? throw new CpfException("CPF9801", "Constraint file not found.");
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null || definition.SqlView is not null || definition.MaximumMembers != 1)
            throw new CpfException("CPF32B2", "Constraint changes require a physical file with MAXMBRS(1).");
        RequireUnenlisted(key.Library, key.Name.Value, "CHGPFCST");
        return definition;
    }

    private SqlConstraintChange ChangeConstraint(QualifiedName key, string name, bool? enabled, bool check, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var allocations = new ConstraintAllocations();
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var original = RequireConstraintFile(connection, transaction, key);
        var definition = FileDefinition.FromJson(original.ToJson());
        var foreign = definition.SqlTable?.ForeignKeys?.SingleOrDefault(item => item.Name == name);
        var predicate = definition.SqlTable?.Checks?.SingleOrDefault(item => item.Name == name);
        if (foreign is null && predicate is null) throw new CpfException("CPF32B2", "Check or referential constraint " + name + " not found on " + key + ".");
        var disabled = foreign?.Disabled ?? predicate!.Disabled; var pending = foreign?.CheckPending ?? predicate!.CheckPending;
        if (enabled is null || enabled == !disabled && (!enabled.Value || !pending) && !(enabled.Value && foreign?.Defined == true))
            return new(name, false, foreign?.Defined ?? false, disabled, pending);
        var locks = new JobLockStore(_factory);
        locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
        Allocate(key);
        var owner = ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner;
        var members = ConstraintMembers(connection, transaction, key);
        var parentSnapshots = new Dictionary<QualifiedName, string>();
        var parentMembers = new Dictionary<QualifiedName, IReadOnlyDictionary<string, string?>>();
        if (foreign is not null)
        {
            var parentKey = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
            var parent = parentKey == key ? original : ReadDefinition(connection, transaction, parentKey.Library, parentKey.Name.Value);
            if (parent is not null)
            {
                RequireUnenlisted(parentKey.Library, parentKey.Name.Value, "CHGPFCST");
                locks.RequireNoPersistentAllocation(new(parentKey.Library, parentKey.Name.Value, FileType));
                if (parentKey != key) Allocate(parentKey);
                parentSnapshots[parentKey] = parent.ToJson();
                parentMembers[parentKey] = ConstraintMembers(connection, transaction, parentKey).ToDictionary(member => member,
                    member => RecordSlots.Identity(connection, transaction, parentKey.Library, parentKey.Name.Value, member));
            }
            if (enabled.Value && foreign.Defined && members.Count == 1 && parent is not null && ConstraintMembers(connection, transaction, parentKey).Count == 1)
            {
                var attempt = FileDefinition.FromJson(definition.ToJson());
                attempt.SqlTable = attempt.SqlTable! with { ForeignKeys = attempt.SqlTable!.ForeignKeys!.Select(item => item.Name == name ? item with { Defined = false } : item).ToArray() };
                try
                {
                    BindSqlForeignKeys(connection, transaction, key, attempt, new(SqlNaming.System, key.Library, _owner, [key.Library]),
                        (original.SqlTable!.ForeignKeys ?? []).Select(item => item.Name!).ToHashSet());
                    foreign = attempt.SqlTable!.ForeignKeys!.Single(item => item.Name == name);
                }
                catch (CpfException error) when (error.MessageId is "SQL0204" or "SQL0538" or "SQL0539" or "SQL7008") { }
            }
            pending = foreign.CheckPending;
            if (!enabled.Value && !foreign.Defined) pending = true;
            if (enabled.Value && !foreign.Defined) pending = check && members.Count != 0 && ForeignRowsInvalid(connection, transaction, key, members[0], definition, foreign, token);
            foreign = foreign with { Disabled = !enabled.Value, CheckPending = pending };
            definition.SqlTable = definition.SqlTable! with { ForeignKeys = definition.SqlTable!.ForeignKeys!.Select(item => item.Name == name ? foreign : item).ToArray() };
        }
        else
        {
            pending = predicate!.CheckPending;
            if (enabled.Value) pending = check && members.Any(member => CheckRowsInvalid(connection, transaction, key, member, definition, predicate, token));
            predicate = predicate with { Disabled = !enabled.Value, CheckPending = pending };
            definition.SqlTable = definition.SqlTable! with { Checks = definition.SqlTable!.Checks!.Select(item => item.Name == name ? predicate : item).ToArray() };
        }
        if (definition.ToJson() == original.ToJson()) return new(name, false, foreign?.Defined ?? false, !enabled.Value, pending,
            enabled.Value && pending ? new CpfException("CPF32B2", "Constraint " + name + " remains check pending.") : null);
        var identities = members.ToDictionary(member => member, member => RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, member)
            ?? throw new CpfException("CPF2817", "Constraint member identity is unavailable."));
        var bound = new BoundTableAlteration(key, original, definition, members, identities, new Dictionary<string, bool>(), new Dictionary<string, SqlAddColumn>());
        var indexes = new List<(string Table, string Sql)>(); var triggers = new List<(string Table, string Sql)>();
        foreach (var member in members) CaptureAlterSchema(connection, transaction, bound, MemberTable(key.Library, key.Name.Value, member), indexes, triggers);
        SqlForeignCatalog.NullHooks(connection, transaction, install: false);
        PublishAlterDefinition(connection, transaction, key, definition);
        foreach (var member in members) RewriteAlterMember(connection, transaction, bound, member, DateTimeOffset.UtcNow, token, trustExistingChecks: true);
        foreach (var (_, sql) in indexes.Concat(triggers)) RunAlterSql(connection, transaction, sql, token);
        SqlForeignCatalog.NullHooks(connection, transaction, install: true);
        foreach (var member in members)
        {
            var table = Quote(MemberTable(key.Library, key.Name.Value, member));
            foreach (var sql in new[] { "INSERT INTO " + table + " DEFAULT VALUES", "DELETE FROM " + table + " WHERE 0",
                "UPDATE " + table + " SET " + string.Join(',', definition.PrimaryFormat.Fields.Select(field => Quote(field.Name) + "=" + Quote(field.Name))) + " WHERE 0" })
                RunAlterSql(connection, transaction, "EXPLAIN " + sql, token);
        }
        RequireConstraintFile(connection, transaction, key);
        if (ReadDefinition(connection, transaction, key.Library, key.Name.Value)?.ToJson() != definition.ToJson() ||
            ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner != owner ||
            !ConstraintMembers(connection, transaction, key).SequenceEqual(members) || identities.Any(item => RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, item.Key) != item.Value) ||
            parentSnapshots.Any(item => item.Key != key && ReadDefinition(connection, transaction, item.Key.Library, item.Key.Name.Value)?.ToJson() != item.Value))
            throw new CpfException("CPF32B2", "Constraint definitions or member identities changed during publication.");
        foreach (var (parentKey, expected) in parentMembers)
            if (!ConstraintMembers(connection, transaction, parentKey).SequenceEqual(expected.Keys) || expected.Any(item =>
                RecordSlots.Identity(connection, transaction, parentKey.Library, parentKey.Name.Value, item.Key) != item.Value))
                throw new CpfException("CPF32B2", "Parent member identities changed during constraint publication.");
        if (enabled.Value && check && !pending)
            foreach (var member in members)
                if (foreign is { Defined: false } && ForeignRowsInvalid(connection, transaction, key, member, definition, foreign, token) ||
                    predicate is not null && CheckRowsInvalid(connection, transaction, key, member, definition, predicate, token))
                    throw new CpfException("CPF32B2", "Data changed during constraint verification.");
        SqlForeignCatalog.ValidateBindings(connection, transaction, checkRows: false);
        ValidateIndexPublisher(connection, transaction, token); token.ThrowIfCancellationRequested(); transaction.Commit();
        var diagnostic = enabled.Value && pending ? new CpfException("CPF32B2", "Constraint " + name + " has violating records and is check pending.") : null;
        return new(name, true, foreign?.Defined ?? false, !enabled.Value, pending, diagnostic);

        void Allocate(QualifiedName file)
        {
            if (OperationIdentity.Current?.Job is { } job)
                allocations.Items.Add(locks.AcquireReferentialFile(job, file, token, () => ValidateIndexPublisher(connection, transaction, token), transaction, JobLockMode.Exclusive));
        }
    }

    private static IReadOnlyList<string> ConstraintMembers(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr";
        command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        var members = new List<string>(); using var reader = command.ExecuteReader(); while (reader.Read()) members.Add(reader.GetString(0));
        if (members.Count > 1) throw new CpfException("CPF32B2", "Constraint files require a maximum of one member.");
        return members;
    }

    private static bool CheckRowsInvalid(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, string member,
        FileDefinition definition, SqlCheckConstraint constraint, CancellationToken token)
    {
        var clause = SqlCheckRuntime.Sql(definition.PrimaryFormat, constraint);
        var start = clause.IndexOf("CHECK(", StringComparison.Ordinal) + 6;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM " + Quote(MemberTable(key.Library, key.Name.Value, member)) + " WHERE (" + clause[start..^1] + ")=0)";
        return SqliteCancellation.Run(connection, token, () => Convert.ToInt32(command.ExecuteScalar()) != 0);
    }

    private static bool ForeignRowsInvalid(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, string member,
        FileDefinition definition, SqlForeignConstraint foreign, CancellationToken token)
    {
        return SqlForeignCatalog.HasViolations(connection, transaction, MemberTable(key.Library, key.Name.Value, member), definition, foreign, token);
    }

    private sealed class ExistingCheckCopyScope : IDisposable
    {
        private readonly SqliteCommand _command; private readonly long _original;
        internal ExistingCheckCopyScope(SqliteConnection connection, SqliteTransaction transaction)
        {
            _command = connection.CreateCommand(); _command.Transaction = transaction; _command.CommandText = "PRAGMA ignore_check_constraints";
            _original = Convert.ToInt64(_command.ExecuteScalar()); _command.CommandText = "PRAGMA ignore_check_constraints=ON"; _command.ExecuteNonQuery();
        }
        public void Dispose()
        { try { _command.CommandText = "PRAGMA ignore_check_constraints=" + _original; _command.ExecuteNonQuery(); } finally { _command.Dispose(); } }
    }

    private sealed class ConstraintAllocations : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() { for (var index = Items.Count - 1; index >= 0; index--) Items[index].Dispose(); }
    }
}
