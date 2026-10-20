using System.Globalization;
using System.Text.Json;
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
    private sealed record BoundTableAlteration(QualifiedName Key, FileDefinition Original, FileDefinition Definition,
        IReadOnlyList<string> Members, IReadOnlyDictionary<string, string> Identities, IReadOnlyDictionary<string, bool> Dropped, IReadOnlyDictionary<string, SqlAddColumn> Added);

    private BoundTableAlteration BindTableAlteration(SqliteConnection connection, SqliteTransaction transaction,
        SqlAlterTable statement, SqlExecutionOptions options)
    {
        var target = ResolveSqlObject(statement.Name, options, false); var key = new QualifiedName(target.Library, target.Name);
        new ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, key.Library, key.Name.Value, AuthorityBit.ObjectAlter);
        var original = ReadDefinition(connection, transaction, key.Library, key.Name.Value) ?? throw new CpfException("SQL0204", "Table not found.");
        if (original.Attribute != FileAttribute.Physical || original.SqlTable is null || original.SqlView is not null || original.Logical is not null || original.Formats.Count != 1)
            throw new CpfException("SQL0104", "ALTER TABLE requires an SQL-created physical table.");
        RequireUnenlisted(key.Library, key.Name.Value, "ALTER TABLE");
        var definition = FileDefinition.FromJson(original.ToJson()); var fields = definition.PrimaryFormat.Fields;
        var keys = definition.SqlTable!.Constraints.ToList(); var checks = (definition.SqlTable.Checks ?? []).ToList(); var foreign = (definition.SqlTable.ForeignKeys ?? []).ToList(); var dropped = new Dictionary<string, bool>(); var added = new Dictionary<string, SqlAddColumn>();
        var alterations = new HashSet<(string, string)>(); var changed = new HashSet<string>();
        var constraintNames = new HashSet<string>();
        foreach (var action in statement.Changes)
        {
            switch (action)
            {
                case SqlAddColumn add:
                    if (fields.Any(field => field.Name == add.Field.Name) || !changed.Add(add.Field.Name)) throw new CpfException("SQL0612", "Repeated column " + add.Field.Name + ".");
                    var before = add.Before is null ? fields.Count : fields.FindIndex(field => field.Name == add.Before && original.PrimaryFormat.Find(field.Name) is not null);
                    if (before < 0) throw new CpfException("SQL0206", "Unknown BEFORE column.");
                    fields.Insert(before, CloneAlterField(add.Field)); added.Add(add.Field.Name, add); break;
                case SqlDropColumn drop:
                    if (!changed.Add(drop.Name)) throw new CpfException("SQL0612", "Column is added, dropped or altered more than once.");
                    var removed = fields.Find(field => field.Name == drop.Name) ?? throw new CpfException("SQL0206", "Unknown dropped column " + drop.Name + ".");
                    fields.Remove(removed); dropped.Add(drop.Name, drop.Cascade); break;
                case SqlAlterColumn alter:
                    if (added.ContainsKey(alter.Name) || dropped.ContainsKey(alter.Name)) throw new CpfException("SQL0612", "An added or dropped column cannot be altered in the same statement.");
                    var current = fields.Find(field => field.Name == alter.Name) ?? throw new CpfException("SQL0206", "Unknown altered column " + alter.Name + ".");
                    changed.Add(alter.Name);
                    if (alter.DataType is { } type)
                    {
                        Unique("TYPE");
                        if (!(current.IsNumeric && type.IsNumeric || current.Type == type.Type)) throw new CpfException("SQL0190", "Incompatible ALTER COLUMN data type.");
                        var next = new FieldSpec { Name = current.Name, Type = type.Type, Length = type.Length, Decimals = type.Decimals,
                            VariableLength = type.VariableLength, Ccsid = type.Ccsid == 0 ? current.Ccsid : type.Ccsid, NullCapable = current.NullCapable,
                            CurrentDatetimeDefault = current.CurrentDatetimeDefault, DefaultValue = current.DefaultValue, Text = current.Text };
                        fields[fields.IndexOf(current)] = current = next;
                    }
                    if (alter.Nullable is { } nullable)
                    {
                        Unique("NULL");
                        if (nullable && keys.Any(constraint => constraint.Primary && constraint.Columns.Contains(alter.Name))) throw new CpfException("SQL0190", "A primary key column cannot drop NOT NULL.");
                        current.NullCapable = nullable;
                    }
                    if (alter.Default is { } value)
                    {
                        Unique("DEFAULT");
                        if (value.Kind == SqlValueKind.Default) value = current.NullCapable ? new(SqlValueKind.Literal) : AlterTypeDefault(current.Type);
                        current = AlterFieldDefault(current, value, options); fields[fields.FindIndex(field => field.Name == alter.Name)] = current;
                    }
                    if (alter.DropDefault)
                    {
                        Unique("DEFAULT"); if (!current.NullCapable) throw new CpfException("SQL0190", "DROP DEFAULT requires a nullable column.");
                        current = AlterFieldDefault(current, new(SqlValueKind.Literal), options); fields[fields.FindIndex(field => field.Name == alter.Name)] = current;
                    }
                    break;
                    void Unique(string attribute)
                    { if (!alterations.Add((alter.Name, attribute))) throw new CpfException("SQL0612", "Repeated ALTER COLUMN attribute."); }
                case SqlAddKey add:
                    if (add.Constraint.Name is { } addedName && !constraintNames.Add(addedName)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                    if (add.Constraint.Columns.Any(column => fields.All(field => field.Name != column))) throw new CpfException("SQL0206", "An added key references a missing column.");
                    keys.Add(add.Constraint); break;
                case SqlAddForeign add:
                    if (add.Constraint.Name is { } foreignName && !constraintNames.Add(foreignName)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                    foreign.Add(add.Constraint); break;
                case SqlDropForeign drop:
                    var removedForeign = foreign.FirstOrDefault(item => item.Name == drop.Name) ?? throw new CpfException("SQL0204", "Foreign constraint not found.");
                    if (!constraintNames.Add(drop.Name)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                    foreign.Remove(removedForeign); break;
                case SqlAddCheck add:
                    if (add.Constraint.Name is { } checkName && !constraintNames.Add(checkName)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                    _ = SqlCheckRuntime.Validate(definition.PrimaryFormat, add.Constraint); checks.Add(add.Constraint); break;
                case SqlDropKey drop:
                    var foreignKey = !drop.Primary && !drop.UniqueOnly && !drop.CheckOnly ? foreign.FirstOrDefault(item => item.Name == drop.Name) : null;
                    if (foreignKey is not null)
                    {
                        if (!constraintNames.Add(foreignKey.Name!)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                        foreign.Remove(foreignKey); break;
                    }
                    var check = !drop.Primary && !drop.UniqueOnly ? checks.FirstOrDefault(item => item.Name == drop.Name) : null;
                    if (check is not null)
                    {
                        if (check.Name is { } removedCheck && !constraintNames.Add(removedCheck)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                        checks.Remove(check); break;
                    }
                    var constraint = drop.CheckOnly ? null : keys.FirstOrDefault(item => drop.Primary ? item.Primary : item.Name == drop.Name && (!drop.UniqueOnly || !item.Primary));
                    if (constraint is null) throw new CpfException("SQL0204", "Constraint not found.");
                    if (constraint.Name is { } removedName && !constraintNames.Add(removedName)) throw new CpfException("SQL0612", "Repeated constraint name in ALTER TABLE.");
                    keys.Remove(constraint); break;
            }
        }
        foreach (var constraint in keys.ToArray())
        {
            if (!constraint.Columns.Any(dropped.ContainsKey)) continue;
            if (!constraint.Columns.All(dropped.ContainsKey) && constraint.Columns.Any(column => dropped.TryGetValue(column, out var cascade) && !cascade))
                throw new CpfException("SQL0478", "A constraint depends on a dropped column.");
            keys.Remove(constraint);
        }
        foreach (var check in checks.ToArray())
        {
            var prior = original.SqlTable!.Checks?.FirstOrDefault(item => item == check);
            var references = SqlCheckRuntime.Validate(prior is null ? definition.PrimaryFormat : original.PrimaryFormat, check);
            if (!references.Any(dropped.ContainsKey)) continue;
            if (!references.All(dropped.ContainsKey) && references.Any(column => dropped.TryGetValue(column, out var cascade) && !cascade))
                throw new CpfException("SQL0478", "A CHECK constraint depends on a dropped column.");
            checks.Remove(check);
        }
        foreach (var constraint in foreign.ToArray())
        {
            if (!constraint.Columns.Any(dropped.ContainsKey)) continue;
            if (!constraint.Columns.All(dropped.ContainsKey) && constraint.Columns.Any(column => dropped.TryGetValue(column, out var cascade) && !cascade))
                throw new CpfException("SQL0478", "A foreign constraint depends on a dropped column.");
            foreign.Remove(constraint);
        }
        if (fields.Count is < 1 or > 8000) throw new CpfException("SQL0104", "ALTER TABLE requires 1–8000 columns.");
        definition.PrimaryFormat.AssignPositions(); if (definition.PrimaryFormat.RecordLength > 32766) throw new CpfException("SQL0904", "Altered record format exceeds its byte limit.");
        foreach (var field in fields)
            if (field.DefaultValue is { } value) _ = SqlFieldValue(field, value.Value);
        if (keys.Count(key => key.Primary) > 1 || keys.Select(key => key.Name).Concat(checks.Select(check => check.Name)).Concat(foreign.Select(item => item.Name)).Where(name => name is not null).GroupBy(name => name).Any(group => group.Count() > 1))
            throw new CpfException("SQL0612", "Repeated primary key or constraint name.");
        var signatures = new HashSet<string>(); var names = SqlConstraintCatalog.Names(connection, transaction, key.Library, key);
        foreach (var constraint in keys)
        {
            if (constraint.Name is { } name && !names.Add(name)) throw new CpfException("SQL0601", "Constraint name already exists in schema.");
            if (constraint.Columns.Count is < 1 or > 120 || constraint.Columns.Distinct().Count() != constraint.Columns.Count || !signatures.Add(string.Join(',', constraint.Columns.Order())))
                throw new CpfException("SQL0612", "Repeated or invalid constraint columns.");
            var columns = constraint.Columns.Select(column => fields.Find(field => field.Name == column) ?? throw new CpfException("SQL0206", "Unknown constraint column " + column + ".")).ToArray();
            if (columns.Sum(field => field.StorageLength + (field.NullCapable ? 1 : 0)) > 32766) throw new CpfException("SQL0904", "Constraint key is too wide.");
        }
        foreach (var check in checks.Where(check => check.Name is not null))
            if (!names.Add(check.Name!)) throw new CpfException("SQL0601", "Constraint name already exists in schema.");
        checks = checks.Select(check => {
            _ = SqlCheckRuntime.Validate(definition.PrimaryFormat, check);
            if (check.Name is not null) return check;
            string name; do { name = "C" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(); } while (!names.Add(name));
            return check with { Name = name };
        }).ToList();
        definition.SqlTable = new(keys, checks.Count == 0 ? null : checks, foreign.Count == 0 ? null : foreign);
        CascadeSelfForeignKeys(key, original, definition, statement.Changes, dropped);
        BindSqlForeignKeys(connection, transaction, key, definition, options, (original.SqlTable!.ForeignKeys ?? []).Select(item => item.Name!).ToHashSet());
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT m.mbr,s.id FROM sys_file_members m LEFT JOIN sys_record_slots s USING(lib,name,type,mbr) WHERE m.lib=$lib AND m.name=$name AND m.type='*FILE' ORDER BY m.created,m.mbr";
        read.Parameters.AddWithValue("$lib", key.Library); read.Parameters.AddWithValue("$name", key.Name.Value);
        var members = new List<string>(); var identities = new Dictionary<string, string>();
        using (var reader = read.ExecuteReader()) while (reader.Read())
        { if (reader.IsDBNull(1)) throw new CpfException("SQL0204", "Table member identity is unavailable."); members.Add(reader.GetString(0)); identities.Add(reader.GetString(0), reader.GetString(1)); }
        if (members.Count > 1 && keys.Count != 0) throw new CpfException("SQL0104", "Multi-member SQL key constraints require file-wide uniqueness support.");
        return new(key, original, definition, members, identities, dropped, added);
    }
    private static FieldSpec CloneAlterField(FieldSpec field) => JsonSerializer.Deserialize<FieldSpec>(JsonSerializer.Serialize(field))!;
    private static SqlValue AlterTypeDefault(FieldType type) => type switch {
        FieldType.Date => new(SqlValueKind.CurrentDate), FieldType.Time => new(SqlValueKind.CurrentTime), FieldType.Timestamp => new(SqlValueKind.CurrentTimestamp),
        FieldType.Alpha => new(SqlValueKind.Literal, ""), _ => new(SqlValueKind.Number, "0") };
    private static FieldSpec AlterFieldDefault(FieldSpec field, SqlValue value, SqlExecutionOptions options)
    {
        if (value.Kind == SqlValueKind.CurrentDate && field.Type != FieldType.Date || value.Kind == SqlValueKind.CurrentTime && field.Type != FieldType.Time || value.Kind == SqlValueKind.CurrentTimestamp && field.Type != FieldType.Timestamp)
            throw new CpfException("SQL0190", "Current datetime default does not match column type.");
        return new() { Name = field.Name, Type = field.Type, Length = field.Length, Decimals = field.Decimals, VariableLength = field.VariableLength,
            Ccsid = field.Ccsid, NullCapable = field.NullCapable, Text = field.Text,
            DefaultValue = value.Kind is SqlValueKind.Literal or SqlValueKind.Number ? new(value.Value is null ? null : Convert.ToString(SqlInputConversion.Normalize(field.Type, value.Value, options.Conversion), CultureInfo.InvariantCulture)) : null,
            CurrentDatetimeDefault = value.Kind is SqlValueKind.CurrentDate or SqlValueKind.CurrentTime or SqlValueKind.CurrentTimestamp };
    }
}
