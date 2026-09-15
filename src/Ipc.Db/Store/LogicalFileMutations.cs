using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Services.Work;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private void MutateLogical(FileDefinition definition, string member, string formatName, IReadOnlyDictionary<string, object?> values, string operation)
    {
        try { MutateLogicalCore(definition, member, formatName, values, operation); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode == 1) { throw LogicalError("Invalid stored logical value or mutation definition."); }
    }
    private void MutateLogicalCore(FileDefinition definition, string member, string formatName, IReadOnlyDictionary<string, object?> values, string operation)
    {
        var logical = definition.Logical!; ValidateLogical(logical);
        var format = definition.PrimaryFormat;
        if (format.Name != formatName || !logical.Members.TryGetValue(member.ToUpperInvariant(), out var members)) throw LogicalError("Invalid logical format or member.");
        if (operation == "INSERT" && members.Count != 1) throw LogicalError("Logical output requires a binding to one physical member.");
        var physical = GetDefinition(logical.SourceLibrary, logical.SourceFile) ?? throw LogicalError("Missing physical definition.");
        if (physical.Logical is not null || physical.PrimaryFormat.Name != logical.SourceFormat) throw LogicalError("Physical format changed; recompile the logical file.");
        if (format.Fields.Any(f => physical.PrimaryFormat.Find(f.Name) is not { } original || original.Type != f.Type || original.Length != f.Length || original.Decimals != f.Decimals || original.Ccsid != f.Ccsid || original.NullCapable != f.NullCapable || original.VariableLength != f.VariableLength || original.DeclaredDigits != f.DeclaredDigits || original.CurrentDatetimeDefault != f.CurrentDatetimeDefault || original.DefaultValue != f.DefaultValue))
            throw LogicalError("Physical fields changed; recompile the logical file.");
        if (values.Keys.Any(k => format.Find(k) is null)) throw LogicalError("Unknown logical field.");
        foreach (var physicalMember in members) EnsureMember(logical.SourceLibrary, logical.SourceFile, physicalMember);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var physicalMember in members) EnsureExactDecimalStorage(connection, transaction, MemberTable(logical.SourceLibrary, logical.SourceFile, physicalMember), physical.PrimaryFormat);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var predicate = LogicalPredicate(command, logical, physical.PrimaryFormat);
        string Parameter(FieldSpec field, object? value)
        {
            if (value is null && !field.NullCapable) throw LogicalError("Null is not allowed for field " + field.Name + ".");
            var parameter = "$write" + command.Parameters.Count;
            command.Parameters.AddWithValue(parameter, RecordSqlValue(field, value) ?? DBNull.Value); return parameter;
        }
        if (operation == "INSERT")
        {
            var fields = physical.PrimaryFormat.Fields;
            var parameters = fields.Select(f => Parameter(f, values.TryGetValue(f.Name, out var value) ? value : DefaultFor(f))).ToArray();
            command.CommandText = "SELECT " + predicate + " FROM (SELECT " + string.Join(',', fields.Select((f, i) => parameters[i] + " AS " + Quote(f.Name))) + ")";
            var selected = Convert.ToInt64(command.ExecuteScalar());
            if (selected != 1) throw LogicalError("Output record does not satisfy the logical selection rules.");
            command.CommandText = "INSERT INTO " + Quote(MemberTable(logical.SourceLibrary, logical.SourceFile, members[0])) + " (" + string.Join(',', fields.Select(f => Quote(f.Name))) + ") VALUES (" + string.Join(',', parameters) + ")";
            command.ExecuteNonQuery(); command.CommandText = "SELECT last_insert_rowid()";
            new JobLockStore(_factory).RequireRecordAccess(logical.SourceLibrary, logical.SourceFile, members[0], Convert.ToInt64(command.ExecuteScalar()), write: true);
        }
        else
        {
            var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToArray();
            if (keys.Length == 0 || keys.Any(k => !values.ContainsKey(k.Name))) throw new CpfException("CPF3201", "Logical mutation requires the complete key.");
            predicate += " AND " + string.Join(" AND ", keys.Select(k => LogicalColumn(k) + " IS " + LogicalExpression(k, Parameter(k, values[k.Name]))));
            var changes = operation == "UPDATE" ? format.Fields.Where(f => f.Sequence == 0 && values.ContainsKey(f.Name)).Select(f => Quote(f.Name) + "=" + Parameter(f, values[f.Name])).ToArray() : Array.Empty<string>();
            if (operation == "UPDATE" && changes.Length == 0) return;
            foreach (var physicalMember in members)
            {
                var table = Quote(MemberTable(logical.SourceLibrary, logical.SourceFile, physicalMember));
                command.CommandText = "SELECT " + RowIdentitySql(physical.PrimaryFormat) + " FROM " + table + " WHERE " + predicate;
                using (var reader = command.ExecuteReader()) while (reader.Read())
                    new JobLockStore(_factory).RequireRecordAccess(logical.SourceLibrary, logical.SourceFile, physicalMember, reader.GetInt64(0), write: true);
               
                command.CommandText = (operation == "DELETE" ? "DELETE FROM " + table : "UPDATE " + table + " SET " + string.Join(',', changes)) + " WHERE " + predicate;
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }
}
