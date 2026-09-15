using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public void CreateLogicalFile(string library, string name, FileDefinition definition, string source, IReadOnlyList<string>? physicalMembers = null, string member = "*FILE", int maximumMembers = 256)
    {
        if (definition.Attribute != FileAttribute.Logical || definition.Logical is not { } logical || definition.Formats.Count != 1 || definition.Name != name)
            throw LogicalError("Invalid logical file definition.");
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        ValidateLogical(logical);
        logical = logical with { UniquePaths = new Dictionary<string, string>() };
        member = member.ToUpperInvariant();
        if (member == "*FILE") member = name;
        if ((member != "*NONE" && !ObjectName.IsValid(member)) || maximumMembers is < 1 or > 256) throw LogicalError("Invalid logical member name or maximum.");
        Authorize(logical.SourceLibrary, logical.SourceFile, AuthorityBit.Read | AuthorityBit.ObjectReference);
        var physical = GetDefinition(logical.SourceLibrary, logical.SourceFile) ?? throw LogicalError("Physical file not found.");
        if (physical.Attribute != FileAttribute.Physical || physical.Formats.Count != 1 || physical.PrimaryFormat.Name != logical.SourceFormat)
            throw LogicalError("Logical source must be a single-format physical file.");
        if (definition.PrimaryFormat.Fields.Count is < 1 or > 1024 || definition.PrimaryFormat.Fields.Any(f => !ObjectName.IsValid(f.Name) || physical.PrimaryFormat.Find(f.Name) is not { } original || original.Type != f.Type || original.Length != f.Length || original.Decimals != f.Decimals || original.Ccsid != f.Ccsid || original.NullCapable != f.NullCapable || original.VariableLength != f.VariableLength || original.DeclaredDigits != f.DeclaredDigits || original.CurrentDatetimeDefault != f.CurrentDatetimeDefault || original.DefaultValue != f.DefaultValue))
            throw LogicalError("Logical fields do not match the physical format.");
        var available = ListMembers(logical.SourceLibrary, logical.SourceFile);
        var members = member == "*NONE" ? Array.Empty<string>() : physicalMembers?.ToArray() ?? available.ToArray();
        if (members.Length > 256 || members.Distinct(StringComparer.Ordinal).Count() != members.Length || members.Any(m => !ObjectName.IsValid(m) || !available.Contains(m)))
            throw LogicalError("Logical member requires at most 256 existing, distinct physical members.");
        if (logical.Unique && members.Length > 1) throw LogicalError("Unique logical members currently require at most one physical member.");
        // Validate all constants now so malformed predicates cannot become delayed runtime failures.
        using (var probe = _factory.Open()) using (var command = probe.CreateCommand()) _ = LogicalPredicate(command, logical, physical.PrimaryFormat);
        var bindings = new Dictionary<string, IReadOnlyList<string>>();
        if (member != "*NONE") bindings[member] = members;
        definition.Logical = logical with { Members = bindings, MaximumMembers = maximumMembers };
        if (_objects.Exists(library, name, FileType)) throw new CpfException("CPF5805", "File already exists.");
        _objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = FileType, Attribute = "*LF", Owner = _owner,
            Source = source, Ccsid = definition.Ccsid, Description = definition.Text, Format = definition.PrimaryFormat.Name }, connection, transaction);
        var uniquePaths = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat, physical.PrimaryFormat, members);
        definition.Logical = definition.Logical with { UniquePaths = uniquePaths };
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO sys_file_defs(lib,name,type,def) VALUES($lib,$name,'*FILE',$def);
            INSERT INTO sys_file_members(lib,name,type,mbr,created) SELECT $lib,$name,'*FILE',$member,$now WHERE $member <> '*NONE';
            INSERT INTO sys_object_dependencies VALUES($lib,$name,'*FILE',$sourceLib,$sourceName,'*FILE');
            """;
        insert.Parameters.AddWithValue("$lib", library); insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$member", member);
        insert.Parameters.AddWithValue("$def", definition.ToJson()); insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$sourceLib", logical.SourceLibrary); insert.Parameters.AddWithValue("$sourceName", logical.SourceFile);
        insert.ExecuteNonQuery(); transaction.Commit();
    }

    private static void ValidateLogical(LogicalFileDefinition logical)
    {
        if (!ObjectName.IsValid(logical.SourceLibrary) || !ObjectName.IsValid(logical.SourceFile) || !ObjectName.IsValid(logical.SourceFormat) ||
            logical.Rules is null || logical.Rules.Count > 256 || logical.MaximumMembers is < 1 or > 256 || logical.Members is null || logical.Members.Count > logical.MaximumMembers ||
            logical.UniquePaths is null || logical.UniquePaths.Count > 256 || (!logical.Unique && (logical.UniquePaths.Count != 0 || logical.ExcludeNullKeys)) ||
            logical.UniquePaths.Any(p => !ObjectName.IsValid(p.Key) || p.Value is null || !p.Value.StartsWith("ipc_unique_", StringComparison.Ordinal) || p.Value.Length != 75 || p.Value[11..].Any(c => !char.IsAsciiHexDigit(c))) ||
            logical.Members.Any(p => !ObjectName.IsValid(p.Key) || p.Value is null || p.Value.Count > 256 || p.Value.Any(m => !ObjectName.IsValid(m))) ||
            logical.Rules.Any(r => r is null || r.Conditions is null || r.Conditions.Count is < 1 or > 256 || r.Conditions.Any(c => c is null || !ObjectName.IsValid(c.Field) || c.Values is null || c.Values.Count is < 1 or > 256 || c.Values.Any(v => v is null || v.Length > 32768))))
            throw LogicalError("Malformed logical file metadata.");
    }
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadLogical(string library, string name, string member, FileDefinition definition, bool keyed, IReadOnlyDictionary<string, object?>? prefix = null)
    {
        try { return ReadLogicalCore(library, name, member, definition, keyed, prefix); }
        catch (SqliteException error) when (error.SqliteErrorCode == 1) { throw LogicalError("Invalid stored logical value or query definition."); }
    }
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadLogicalCore(string library, string name, string member, FileDefinition definition, bool keyed, IReadOnlyDictionary<string, object?>? prefix)
    {
        var logical = definition.Logical!; ValidateLogical(logical);
        if (!logical.Members.TryGetValue(member.ToUpperInvariant(), out var members)) throw new CpfException("CPF2817", "Logical member has no physical member binding.");
        if (members.Count == 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();
        var physical = GetDefinition(logical.SourceLibrary, logical.SourceFile) ?? throw LogicalError("Missing physical file.");
        if (physical.Logical is not null || physical.PrimaryFormat.Name != logical.SourceFormat) throw LogicalError("Physical format changed; recompile the logical file.");
        var format = definition.PrimaryFormat;
        if (format.Fields.Any(f => !ObjectName.IsValid(f.Name) || physical.PrimaryFormat.Find(f.Name) is not { } original || original.Type != f.Type || original.Length != f.Length || original.Decimals != f.Decimals || original.Ccsid != f.Ccsid || original.NullCapable != f.NullCapable || original.VariableLength != f.VariableLength || original.DeclaredDigits != f.DeclaredDigits || original.CurrentDatetimeDefault != f.CurrentDatetimeDefault || original.DefaultValue != f.DefaultValue))
            throw LogicalError("Physical fields changed; recompile the logical file.");
        foreach (var physicalMember in members) EnsureMember(logical.SourceLibrary, logical.SourceFile, physicalMember);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        var predicate = LogicalPredicate(command, logical, physical.PrimaryFormat);
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToArray();
        if (prefix is not null)
        {
            var conditions = new List<string>();
            foreach (var key in keys)
            {
                if (!prefix.TryGetValue(key.Name, out var value)) break;
                var parameter = "$key" + conditions.Count; command.Parameters.AddWithValue(parameter, RecordSqlValue(key, value) ?? DBNull.Value);
                conditions.Add(LogicalColumn(key) + " IS " + LogicalExpression(key, parameter));
            }
            if (conditions.Count == 0) throw new CpfException("CPF3201", "No logical key prefix supplied.");
            predicate += " AND " + string.Join(" AND ", conditions);
        }
        var fields = string.Join(',', format.Fields.Select(f => Quote(f.Name)));
        command.CommandText = string.Join(" UNION ALL ", members.Select((m, index) => $"SELECT {fields},{RowIdentitySql(physical.PrimaryFormat)} AS __ipc_record_number__,{index} AS __ipc_member_order__ FROM {Quote(MemberTable(logical.SourceLibrary, logical.SourceFile, m))} WHERE {predicate}"));
        command.CommandText = "SELECT * FROM (" + command.CommandText + ") ORDER BY " + (keyed && keys.Length > 0 ? string.Join(',', keys.Select(OrderKey)) + "," : "") + "__ipc_member_order__,__ipc_record_number__";
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var index = 0; index < format.Fields.Count; index++)
            {
                if (reader.IsDBNull(index) && !format.Fields[index].NullCapable) throw LogicalError("Null stored in a non-null logical field.");
                _ = RecordSqlValue(format.Fields[index], reader.IsDBNull(index) ? null : reader.GetValue(index));
            }
            new JobLockStore(_factory).RequireRecordAccess(logical.SourceLibrary, logical.SourceFile, members[reader.GetInt32(format.Fields.Count + 1)], reader.GetInt64(format.Fields.Count), write: false);
            rows.Add(ReadRow(reader, format));
        }
        return rows;
    }
    private static string LogicalPredicate(SqliteCommand command, LogicalFileDefinition logical, RecordFormat physical, bool inlineConstants = false)
    {
        if (logical.Rules.Count == 0) return logical.DefaultSelect ? "1" : "0";
        var clauses = new List<string>();
        foreach (var rule in logical.Rules)
        {
            var conditions = new List<string>();
            foreach (var condition in rule.Conditions)
            {
                var field = physical.Find(condition.Field) ?? throw LogicalError("Selection field not found.");
                var values = new List<string>();
                foreach (var raw in condition.Values)
                {
                    if (inlineConstants) { values.Add(LogicalExpression(field, LogicalLiteral(RecordSqlValue(field, raw)))); continue; }
                    var parameter = "$lf" + command.Parameters.Count;
                    try { command.Parameters.AddWithValue(parameter, RecordSqlValue(field, raw) ?? DBNull.Value); }
                    catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { throw LogicalError("Invalid selection constant for " + field.Name + "."); }
                    values.Add(LogicalExpression(field, parameter));
                }
                var column = LogicalColumn(field);
                var expression = condition.Operator switch
                {
                    "EQ" or "NE" or "GT" or "GE" or "LT" or "LE" when values.Count == 1 => column + " " + (condition.Operator switch { "EQ" => "=", "NE" => "<>", "GT" => ">", "GE" => ">=", "LT" => "<", _ => "<=" }) + " " + values[0],
                    "RANGE" when values.Count == 2 => column + " BETWEEN " + values[0] + " AND " + values[1],
                    "VALUES" => column + " IN (" + string.Join(',', values) + ")",
                    _ => throw LogicalError("Unsupported logical selection predicate."),
                };
                conditions.Add("(" + expression + ")");
            }
            clauses.Add("WHEN " + string.Join(" AND ", conditions) + " THEN " + (rule.Select ? "1" : "0"));
        }
        return "(CASE " + string.Join(' ', clauses) + " ELSE " + (logical.DefaultSelect ? "1" : "0") + " END)=1";
    }
    private void AuthorizeLogicalSource(string library, string name, AuthorityBit permission)
    {
        using var connection = _factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT json_extract(def,'$.logical.sourceLibrary'),json_extract(def,'$.logical.sourceFile') FROM sys_file_defs WHERE lib=$lib AND name=$name AND type='*FILE'";
        query.Parameters.AddWithValue("$lib", library.ToUpperInvariant()); query.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return;
        var sourceLibrary = reader.GetString(0); var sourceFile = reader.GetString(1);
        if (!ObjectName.IsValid(sourceLibrary) || !ObjectName.IsValid(sourceFile)) throw LogicalError("Invalid logical source identity.");
        var data = permission & (AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete);
        new ServiceAuthorization(_factory).RequireObject(sourceLibrary, sourceFile, FileType, data | AuthorityBit.ObjectOperate);
    }
    private static CpfException LogicalError(string text) => new("IPC0138", text);
    private static string LogicalLiteral(object? value) => value switch {
        null => "NULL", string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        decimal number => "'" + number.ToString(CultureInfo.InvariantCulture) + "'",
        long number => number.ToString(CultureInfo.InvariantCulture), double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw LogicalError("Unsupported selection constant.") };
    private static string LogicalColumn(FieldSpec field) => LogicalExpression(field, Quote(field.Name));
    private static string LogicalExpression(FieldSpec field, string operand) => field.Type switch {
        FieldType.Alpha => $"ipc_text_key_v1({operand},{field.Ccsid},{field.Length})",
        FieldType.Packed or FieldType.Zoned => $"ipc_decimal_key_v1({operand})", _ => operand };

    private static object? RecordSqlValue(FieldSpec field, object? value)
    {
        if (value is null) return field.NullCapable ? null : ToSqlValue(field, DefaultFor(field));
        if (field.Type == FieldType.Alpha)
        {
            try { _ = DatabaseSortKeys.Text(Convert.ToString(value, CultureInfo.InvariantCulture), field.Ccsid, field.Length); }
            catch (ArgumentException) { throw LogicalError("Invalid character value for field " + field.Name + "."); }
        }
        if (field.Type == FieldType.Time && value is TimeSpan or TimeOnly or DateTimeOffset or DateTime)
        {
            var time = ToTime(value);
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) || time.Ticks % TimeSpan.TicksPerSecond != 0) throw LogicalError("Time value exceeds field precision.");
            return ToSqlValue(field, value);
        }
        if (field.Type is FieldType.Date or FieldType.Timestamp && value is DateTimeOffset or DateTime or DateOnly)
        {
            if (field.Type == FieldType.Timestamp && ToDate(value).Ticks % (field.Length == 26 ? 10 : TimeSpan.TicksPerSecond) != 0) throw LogicalError("Timestamp exceeds field precision.");
            return ToSqlValue(field, value);
        }
        if (field.Type == FieldType.Binary && value is decimal integral && decimal.Truncate(integral) == integral && integral is >= long.MinValue and <= long.MaxValue) value = decimal.ToInt64(integral);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (field.Type is FieldType.Packed or FieldType.Zoned)
        {
            try { _ = DatabaseSortKeys.Decimal(text); }
            catch (ArgumentException) { throw LogicalError("Invalid decimal precision for field " + field.Name + "."); }
            var numeric = decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (field.Decimals is < 0 or > 28 || field.Length < field.Decimals || field.Length > 29 || decimal.Round(numeric, field.Decimals) != numeric ||
                Math.Abs(decimal.Truncate(numeric)).ToString("0", CultureInfo.InvariantCulture).TrimStart('0').Length > field.Length - field.Decimals)
                throw LogicalError("Decimal value exceeds field precision or scale: " + field.Name + ".");
        }
        var valid = field.Type switch
        {
            FieldType.Alpha => text.Length <= field.Length,
            FieldType.Packed or FieldType.Zoned => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
            FieldType.Binary => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var binary) && field.Length switch { 2 => binary is >= short.MinValue and <= short.MaxValue, 4 => binary is >= int.MinValue and <= int.MaxValue, 8 => true, _ => false },
            FieldType.Float => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating) && (field.Length == 8 || field.Length == 4 && float.IsFinite((float)floating)),
            FieldType.Logic => text is "0" or "1" or "True" or "False" or "true" or "false",
            FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.Time => TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            FieldType.Timestamp => DateTime.TryParseExact(text, field.Length == 26 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            _ => false,
        };
        if (!valid) throw LogicalError("Invalid value for field " + field.Name + ".");
        return ToSqlValue(field, field.Type == FieldType.Logic ? text is "1" or "True" or "true" : value);
    }
}
