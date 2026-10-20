using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record EstablishmentSnapshot(string Definition, string Owner, IReadOnlyDictionary<string, string?> Members);

    private void RequireEstablishmentAvailable(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName root, FileDefinition proposed, CancellationToken token)
    {
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        var related = new HashSet<QualifiedName>();
        foreach (var (key, original) in files)
        {
            var child = key == root ? proposed : original;
            if (child.MaximumMembers != 1 || child.SqlTable is null) continue;
            foreach (var foreign in (child.SqlTable.ForeignKeys ?? []).Where(item => item.Defined &&
                         (key == root || item.Parent.Schema == root.Library && item.Parent.Name == root.Name.Value)))
            {
                token.ThrowIfCancellationRequested();
                var parentKey = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
                var parent = parentKey == root ? proposed : files.GetValueOrDefault(parentKey);
                if (parent is null || parent.MaximumMembers != 1 || parent.SqlTable is null) continue;
                if (key != root && ConstraintMembers(connection, transaction, key).Count != 1 ||
                    parentKey != root && ConstraintMembers(connection, transaction, parentKey).Count != 1) continue;
                try { SqlForeignCatalog.ValidateReference(child, foreign with { ParentColumns = foreign.ParentColumns ?? parent.SqlTable.Constraints.FirstOrDefault(item => item.Primary)?.Columns }, parent); }
                catch (CpfException error) when (error.MessageId is "SQL0538" or "SQL0539" or "SQL7008") { continue; }
                related.Add(key); related.Add(parentKey);
            }
        }
        foreach (var key in related)
        {
            RequireUnenlisted(key.Library, key.Name.Value, "Automatic referential establishment");
            new JobLockStore(_factory).RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
        }
    }

    // Called inside the initiating DDL writer. Establishment delegates the related
    // file changes; it does not require the initiator to hold child data authority.
    private bool EstablishDefinedForeignKeys(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName root, ConstraintAllocations allocations, CancellationToken token)
    {
        var files = SqlForeignCatalog.Files(connection, transaction).ToDictionary(file => file.Key, file => file.Definition);
        var plans = new List<BoundTableAlteration>();
        foreach (var (key, original) in files)
        {
            var candidates = (original.SqlTable?.ForeignKeys ?? []).Where(item => item.Defined &&
                (key == root || item.Parent.Schema == root.Library && item.Parent.Name == root.Name.Value)).ToArray();
            if (candidates.Length == 0 || original.MaximumMembers != 1) continue;
            var members = ConstraintMembers(connection, transaction, key); if (members.Count != 1) continue;
            var next = FileDefinition.FromJson(original.ToJson());
            var previous = (original.SqlTable!.ForeignKeys ?? []).Select(item => item.Name!).ToHashSet();
            foreach (var foreign in candidates)
            {
                token.ThrowIfCancellationRequested();
                var parentKey = new QualifiedName(foreign.Parent.Schema!, foreign.Parent.Name);
                if (!files.TryGetValue(parentKey, out var parent) || parent.MaximumMembers != 1 ||
                    parent.SqlTable is null || parent.Logical is not null || parent.SqlView is not null) continue;
                var parentMembers = ConstraintMembers(connection, transaction, parentKey); if (parentMembers.Count != 1) continue;
                var attempt = FileDefinition.FromJson(next.ToJson());
                attempt.SqlTable = attempt.SqlTable! with { ForeignKeys = attempt.SqlTable!.ForeignKeys!.Select(item => item.Name == foreign.Name ?
                    item with { Defined = false, ParentMember = parentMembers[0], CheckPending = false } : item).ToArray() };
                try
                {
                    BindSqlForeignKeys(connection, transaction, key, attempt, new(SqlNaming.System, key.Library, _owner, [key.Library]), previous);
                }
                catch (CpfException error) when (error.MessageId is "SQL0204" or "SQL0206" or "SQL0538" or "SQL0539" or "SQL7008") { continue; }
                var bound = attempt.SqlTable!.ForeignKeys!.Single(item => item.Name == foreign.Name);
                // Disabled established relations require verification before they
                // can enforce again. Their paths remain maintained in this state.
                var pending = bound.Disabled || ForeignRowsInvalid(connection, transaction, key, members[0], attempt, bound, token);
                next.SqlTable = next.SqlTable! with { ForeignKeys = next.SqlTable!.ForeignKeys!.Select(item => item.Name == foreign.Name ? bound with { CheckPending = pending } : item).ToArray() };
            }
            if (next.ToJson() == original.ToJson()) continue;
            RequireUnenlisted(key.Library, key.Name.Value, "Automatic referential establishment");
            var identities = members.ToDictionary(member => member, member => RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, member)
                ?? throw new CpfException("SQL0204", "Dependent member identity is unavailable."));
            plans.Add(new(key, original, next, members, identities, new Dictionary<string, bool>(), new Dictionary<string, SqlAddColumn>()));
        }
        if (plans.Count == 0) return false;

        var expected = new Dictionary<QualifiedName, EstablishmentSnapshot>();
        Capture(root);
        foreach (var plan in plans)
        {
            Capture(plan.Key);
            foreach (var foreign in (plan.Definition.SqlTable!.ForeignKeys ?? []).Where(item => !item.Defined))
                Capture(new(foreign.Parent.Schema!, foreign.Parent.Name));
        }
        var locks = new JobLockStore(_factory);
        foreach (var key in expected.Keys)
        {
            RequireUnenlisted(key.Library, key.Name.Value, "Automatic referential establishment");
            locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
        }
        foreach (var plan in plans)
        {
            foreach (var logical in SqlTableLogicalDependents(connection, transaction, plan.Key.Library, plan.Key.Name.Value))
                locks.RequireNoPersistentAllocation(new(logical.Library, logical.Name.Value, FileType));
            // An older physical parent may need its registered collation/index
            // representation upgraded before the new foreign key can refer to it.
            EnsureForeignParents(connection, transaction, plan.Key, plan.Definition, token);
        }
        foreach (var key in expected.Keys.OrderBy(item => item.Library, StringComparer.Ordinal).ThenBy(item => item.Name.Value, StringComparer.Ordinal))
            if (OperationIdentity.Current?.Job is { } job)
                allocations.Items.Add(locks.AcquireReferentialFile(job, key, token, () => ValidateIndexPublisher(connection, transaction, token), transaction, JobLockMode.Exclusive));

        SqlForeignCatalog.NullHooks(connection, transaction, install: false);
        ApplyIncomingForeignChanges(connection, transaction, plans, token, allocated: true, trustExistingChecks: true);
        SqlForeignCatalog.NullHooks(connection, transaction, install: true);
        foreach (var plan in plans)
        {
            expected[plan.Key] = expected[plan.Key] with { Definition = plan.Definition.ToJson() };
            foreach (var foreign in (plan.Definition.SqlTable!.ForeignKeys ?? []).Where(SqlForeignSchema.Enforced))
                if (ForeignRowsInvalid(connection, transaction, plan.Key, plan.Members[0], plan.Definition, foreign, token))
                    throw new CpfException("SQL0530", "Dependent data changed during automatic constraint establishment.", sqlState: "23503");
            foreach (var member in plan.Members)
            {
                var table = Quote(MemberTable(plan.Key.Library, plan.Key.Name.Value, member));
                foreach (var sql in new[] { "INSERT INTO " + table + " DEFAULT VALUES", "DELETE FROM " + table + " WHERE 0",
                    "UPDATE " + table + " SET " + string.Join(',', plan.Definition.PrimaryFormat.Fields.Select(field => Quote(field.Name) + "=" + Quote(field.Name))) + " WHERE 0" })
                    RunAlterSql(connection, transaction, "EXPLAIN " + sql, token);
            }
        }
        foreach (var (key, snapshot) in expected)
            if (ReadDefinition(connection, transaction, key.Library, key.Name.Value)?.ToJson() != snapshot.Definition ||
                ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner != snapshot.Owner ||
                !ConstraintMembers(connection, transaction, key).SequenceEqual(snapshot.Members.Keys) || snapshot.Members.Any(item =>
                    RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, item.Key) != item.Value))
                throw new CpfException("SQL0104", "Related definitions, ownership or member identities changed during automatic establishment.");
        SqlForeignCatalog.ValidateBindings(connection, transaction, checkRows: false);
        ValidateIndexPublisher(connection, transaction, token);
        return true;

        void Capture(QualifiedName key)
        {
            if (expected.ContainsKey(key)) return;
            var definition = ReadDefinition(connection, transaction, key.Library, key.Name.Value) ?? throw new CpfException("SQL0204", "Related file disappeared during establishment.");
            var members = ConstraintMembers(connection, transaction, key).ToDictionary(member => member,
                member => RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, member));
            expected.Add(key, new(definition.ToJson(), ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner, members));
        }
    }
}
