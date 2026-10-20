using System.Globalization;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private void AlterSqlTable(SqlAlterTable statement, SqlExecutionOptions options, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var establishment = new ConstraintAllocations();
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var now = DateTimeOffset.UtcNow; var bound = BindTableAlteration(connection, transaction, statement, options); var key = bound.Key;
        var foreignChanges = PlanIncomingForeignChanges(connection,transaction,key,bound.Original,bound.Definition,statement.Changes,bound.Dropped);
        if (bound.Members.Count != 0) RequireEstablishmentAvailable(connection, transaction, key, bound.Definition, token);
        SqlConstraintCatalog.ValidateLimits(connection, transaction, foreignChanges.Select(change => (change.Key, change.Definition)).Append((key, bound.Definition)).ToArray());
        var owner = ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner;
        var locks = new JobLockStore(_factory); locks.RequireNoPersistentAllocation(new(key.Library, key.Name.Value, FileType));
        var dependents = SqlTableLogicalDependents(connection, transaction, key.Library, key.Name.Value);
        foreach (var dependent in dependents) locks.RequireNoPersistentAllocation(new(dependent.Library, dependent.Name.Value, FileType));
        ValidatePublishedViews(connection, transaction, key, options, token);
        var dependentPlan = PlanAlterDependents(connection, transaction, bound, dependents, token);
        var indexes = new List<(string Table, string Sql)>(); var triggers = new List<(string Table, string Sql)>();
        foreach (var member in bound.Members)
        {
            token.ThrowIfCancellationRequested(); var table = MemberTable(key.Library, key.Name.Value, member);
            CaptureAlterSchema(connection, transaction, bound, table, indexes, triggers);
        }
        EnsureForeignParents(connection, transaction, key, bound.Definition, token);
        SqlForeignCatalog.NullHooks(connection, transaction, install: false);
        // Publish metadata before rebuilding journal hooks and dependent external formats.
        ApplyIncomingForeignChanges(connection,transaction,foreignChanges,token);
        PublishAlterDefinition(connection, transaction, key, bound.Definition);
        foreach (var member in bound.Members) RewriteAlterMember(connection, transaction, bound, member, now, token);
        foreach (var (_, sql) in indexes) RunAlterSql(connection, transaction, sql, token);
        PublishAlterDependents(connection, transaction, bound, dependents, dependentPlan);
        foreach (var (_, sql) in triggers) RunAlterSql(connection, transaction, sql, token);
        SqlForeignCatalog.NullHooks(connection, transaction, install: true);
        foreach (var member in bound.Members)
        {
            var table = MemberTable(key.Library, key.Name.Value, member);
            // SQLite compiles transition-column references here without running hooks.
            foreach (var sql in new[] { "DELETE FROM " + Quote(table) + " WHERE 0", "UPDATE " + Quote(table) + " SET " + string.Join(',', bound.Definition.PrimaryFormat.Fields.Select(field => Quote(field.Name) + "=" + Quote(field.Name))) + " WHERE 0",
                "INSERT INTO " + Quote(table) + " DEFAULT VALUES" })
                RunAlterSql(connection, transaction, "EXPLAIN " + sql, token);
        }
        ValidatePublishedViews(connection, transaction, key, options, token);
        new ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, key.Library, key.Name.Value, AuthorityBit.ObjectAlter);
        if (ReadDefinition(connection, transaction, key.Library, key.Name.Value)?.ToJson() != bound.Definition.ToJson() ||
            ObjectSigningService.Read(connection, transaction, key.Library, key.Name.Value, FileType).Owner != owner)
            throw new CpfException("SQL0104", "ALTER TABLE publication changed the intended definition or ownership.");
        using (var members = connection.CreateCommand())
        {
            members.Transaction = transaction; members.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr";
            members.Parameters.AddWithValue("$lib", key.Library); members.Parameters.AddWithValue("$name", key.Name.Value);
            var actual = new List<string>(); using (var reader = members.ExecuteReader()) while (reader.Read()) actual.Add(reader.GetString(0));
            if (!actual.SequenceEqual(bound.Members) || actual.Any(member => RecordSlots.Identity(connection, transaction, key.Library, key.Name.Value, member) != bound.Identities[member]))
                throw new CpfException("SQL0104", "ALTER TABLE member identities changed during publication.");
        }
        var intended = bound.Definition.ToJson();
        BindSqlForeignKeys(connection,transaction,key,bound.Definition,options,(bound.Original.SqlTable!.ForeignKeys ?? []).Select(item => item.Name!).ToHashSet());
        if (bound.Definition.ToJson() != intended) throw new CpfException("SQL0104","Referential publication changed the intended constraint binding.");
        EstablishDefinedForeignKeys(connection, transaction, key, establishment, token);
        new ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, key.Library, key.Name.Value, AuthorityBit.ObjectAlter);
        SqlForeignCatalog.ValidateBindings(connection,transaction);
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }
    private static void PublishAlterDefinition(SqliteConnection connection, SqliteTransaction transaction, QualifiedName key, FileDefinition definition)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$def", definition.ToJson()); command.Parameters.AddWithValue("$lib", key.Library); command.Parameters.AddWithValue("$name", key.Name.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }
    private static void CaptureAlterSchema(SqliteConnection connection, SqliteTransaction transaction, BoundTableAlteration bound, string table,
        List<(string Table, string Sql)> indexes, List<(string Table, string Sql)> triggers)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table"; command.Parameters.AddWithValue("$table", table);
        var original = command.ExecuteScalar() as string ?? throw new CpfException("SQL0204", "Physical member not found.");
        var expected = CreateMemberTableSql(bound.Original.PrimaryFormat, table, bound.Original.SqlTable);
        if (original[original.IndexOf('(')..] != expected[expected.IndexOf('(')..] &&
            original[original.IndexOf('(')..] != CreateMemberTableSql(bound.Original.PrimaryFormat, table, bound.Original.SqlTable, legacyCollations: true)[expected.IndexOf('(')..])
            throw new CpfException("SQL0104", "ALTER TABLE cannot discard unregistered physical schema constraints or columns.");
        // Cross-table native referential metadata is implemented separately; reject
        // trusted SQLite relationships before DROP can fire cascading actions.
        command.CommandText = "SELECT count(*) FROM sqlite_schema s JOIN pragma_foreign_key_list(s.name) f WHERE s.type='table' AND (s.name=$table OR f.\"table\"=$table)";
        if (SqlForeignCatalog.HasUnregisteredRelations(connection, transaction, table)) throw new CpfException("SQL0478", "ALTER TABLE requires native referential-schema coordination for this member.");
        if (MemberTableDependencies.HasExternalReferences(connection, transaction, [table], (type,name,owner,sql) => SqlForeignCatalog.IsManagedSchema(connection,transaction,type,name,owner,sql)))
            throw new CpfException("SQL0478", "ALTER TABLE requires dependency metadata for external physical SQL references.");
        command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE tbl_name=$table AND type IN ('index','trigger') ORDER BY type,name";
        var schema = new List<(string Type, string Name, string Sql)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            if (reader.IsDBNull(2)) throw new CpfException("SQL0104", "Unregistered implicit physical indexes cannot be discarded.");
            schema.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var item in schema)
        {
            if (item.Type == "trigger")
            {
                if (JournalHooks.OwnsTrigger(connection, transaction, item.Name) || RecordSlots.OwnsTrigger(connection, transaction, item.Name) || SqlForeignCatalog.IsManagedSchema(connection,transaction,"trigger",item.Name,table,item.Sql)) continue;
                if (bound.Dropped.Count != 0) throw new CpfException("SQL0478", "Dropping columns with unregistered application triggers requires native trigger dependency metadata.");
                triggers.Add((table, item.Sql));
            }
            else if (!(item.Name.Contains("ipc_sql_unique_", StringComparison.Ordinal) || item.Name.Contains("ipc_sql_reference_", StringComparison.Ordinal) || item.Name.Contains("ipc_sql_foreign_",StringComparison.Ordinal)) && !item.Name.StartsWith("ipc_path_", StringComparison.Ordinal) && !item.Name.StartsWith("ipc_unique_", StringComparison.Ordinal))
            {
                if (bound.Dropped.Count != 0) throw new CpfException("SQL0478", "Dropping columns with unregistered indexes requires native index dependency metadata.");
                indexes.Add((table, item.Sql));
            }
        }
    }
    private void RewriteAlterMember(SqliteConnection connection, SqliteTransaction transaction, BoundTableAlteration bound, string member, DateTimeOffset now, CancellationToken token, bool trustExistingChecks = false)
    {
        using var checks = trustExistingChecks ? new ExistingCheckCopyScope(connection, transaction) : null;
        using var foreignDdl = new ForeignDdlScope(connection);
        var key = bound.Key; var table = MemberTable(key.Library, key.Name.Value, member); var staging = "ipc_alter_" + Guid.NewGuid().ToString("N");
        var original = bound.Original.PrimaryFormat; var format = bound.Definition.PrimaryFormat;
        RunAlterSql(connection, transaction, CreateMemberTableSql(format, staging, bound.Definition.SqlTable), token);
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT " + Quote(RowIdentitySql(original)) + "," + string.Join(',', original.Fields.Select(field => Quote(field.Name))) + " FROM " + Quote(table) + " ORDER BY " + Quote(RowIdentitySql(original));
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO " + Quote(staging) + "(" + Quote(RowIdentitySql(format)) + "," + string.Join(',', format.Fields.Select(field => Quote(field.Name))) + ") VALUES($rrn," + string.Join(',', format.Fields.Select((_, ordinal) => "$v" + ordinal)) + ")";
        insert.Parameters.AddWithValue("$rrn", 0L); for (var ordinal = 0; ordinal < format.Fields.Count; ordinal++) insert.Parameters.AddWithValue("$v" + ordinal, DBNull.Value);
        SqliteCancellation.Run(connection, token, () => {
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint(); insert.Parameters["$rrn"].Value = reader.GetInt64(0);
                for (var ordinal = 0; ordinal < format.Fields.Count; ordinal++)
                {
                    var field = format.Fields[ordinal]; var previous = original.Fields.FindIndex(item => item.Name == field.Name); object? value;
                    if (previous >= 0) value = reader.IsDBNull(previous+1) ? null : reader.GetValue(previous+1);
                    else if (bound.Added[field.Name].HistoricalDatetimeDefault)
                        value = field.Type switch { FieldType.Date => "0001-01-01", FieldType.Time => "00:00:00", _ => "0001-01-01 00:00:00.000000" };
                    else value = field.CurrentDatetimeDefault ? SqlNow(field.Type, now) : DefaultFor(field);
                    if (previous >= 0 && field.Type == FieldType.Alpha && value is string text) value = AlterCharacterValue(field, text);
                    if (value is not null && field.Type is FieldType.Binary or FieldType.Packed or FieldType.Zoned)
                        value = SqlExactNumber.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!).Rescale(field.Type == FieldType.Binary ? 0 : field.Decimals).ToString();
                    insert.Parameters["$v" + ordinal].Value = SqlFieldValue(field, value) ?? DBNull.Value;
                }
                insert.ExecuteNonQuery();
            }
            return 0;
        });
        RunAlterSql(connection, transaction, "DROP TABLE " + Quote(table), token);
        RunAlterSql(connection, transaction, "ALTER TABLE " + Quote(staging) + " RENAME TO " + Quote(table), token);
        CreateSqlConstraints(connection, transaction, table, bound.Definition);
        using var cleanup = connection.CreateCommand(); cleanup.Transaction = transaction;
        cleanup.CommandText = "DELETE FROM sys_journal_triggers WHERE library=$lib AND file=$name AND member=$member; DELETE FROM sys_record_slot_triggers WHERE member_id=(SELECT id FROM sys_record_slots WHERE lib=$lib AND name=$name AND type='*FILE' AND mbr=$member)";
        cleanup.Parameters.AddWithValue("$lib", key.Library); cleanup.Parameters.AddWithValue("$name", key.Name.Value); cleanup.Parameters.AddWithValue("$member", member); cleanup.ExecuteNonQuery();
        JournalHooks.InstallMember(connection, transaction, key.Library, key.Name.Value, member);
    }
    private static string AlterCharacterValue(FieldSpec field, string text)
    {
        try
        {
            var encoding = (Encoding)CodePage.FromCcsid(field.Ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            var result = new StringBuilder(); var bytes = 0;
            foreach (var rune in text.EnumerateRunes())
            { var value = rune.ToString(); var size = encoding.GetByteCount(value); if (bytes+size > field.Length) break; result.Append(value); bytes += size; }
            return result.ToString();
        }
        catch (EncoderFallbackException) { throw new CpfException("SQL0332", "Altered character data is not representable in its column CCSID."); }
    }
    private static void RunAlterSql(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; SqliteCancellation.Run(connection, token, command.ExecuteNonQuery); }
}
