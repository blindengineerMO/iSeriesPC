using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed class SqliteFileStore
{
    private const string FileType = "*FILE";
    private const string SourceAttribute = "*SRCPF";
    private const string PhysicalAttribute = "*PF";

    private readonly SqliteConnectionFactory _factory;
    private readonly IObjectStore _objects;

    public SqliteFileStore(SqliteConnectionFactory factory, IObjectStore objects)
    {
        _factory = factory;
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    }

    public static string MemberTable(string library, string name, string member) =>
        (library + "." + name + "." + member).ToUpperInvariant();

    public bool FileExists(string library, string name) =>
        _objects.Exists(library, name, FileType);

    public FileDefinition? GetDefinition(string library, string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT def FROM sys_file_defs WHERE lib = $lib AND name = $name AND type = '*FILE'";
        cmd.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : FileDefinition.FromJson(json);
    }

    public void CreatePhysicalFile(string library, string name, FileDefinition definition, string source, string? description = null)
    {
        CreateFileCore(library, name, PhysicalAttribute, definition, source, description);
        AddMember(library, name, name);
    }

    public void CreateSourceFile(string library, string name, string? description = null)
    {
        var definition = SourceFormat(name);
        CreateFileCore(library, name, SourceAttribute, definition, null, description);
        AddMember(library, name, name);
    }

    public bool MemberExists(string library, string name, string member)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib = $lib AND name = $name AND type = '*FILE' AND mbr = $mbr";
        cmd.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$mbr", member.ToUpperInvariant());
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public IReadOnlyList<string> ListMembers(string library, string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT mbr FROM sys_file_members WHERE lib = $lib AND name = $name AND type = '*FILE' ORDER BY mbr";
        cmd.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        using var reader = cmd.ExecuteReader();
        var members = new List<string>();
        while (reader.Read())
        {
            members.Add(reader.GetString(0));
        }

        return members;
    }

    public void AddMember(string library, string name, string member)
    {
        var libraryName = library.ToUpperInvariant();
        var fileName = name.ToUpperInvariant();
        var memberName = member.ToUpperInvariant();
        var definition = GetDefinition(libraryName, fileName)
            ?? throw new CpfException("CPF9801", $"File {fileName} in library {libraryName} not found.");

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sys_file_members (lib, name, type, mbr, created)
                VALUES ($lib, $name, '*FILE', $mbr, $created)
                ON CONFLICT(lib, name, type, mbr) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$lib", libraryName);
            insert.Parameters.AddWithValue("$name", fileName);
            insert.Parameters.AddWithValue("$mbr", memberName);
            insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(libraryName, fileName, memberName));
            create.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void RemoveMember(string library, string name, string member)
    {
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE IF EXISTS " + Quote(MemberTable(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant()));
            drop.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sys_file_members WHERE lib = $lib AND name = $name AND type = '*FILE' AND mbr = $mbr";
            delete.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
            delete.Parameters.AddWithValue("$name", name.ToUpperInvariant());
            delete.Parameters.AddWithValue("$mbr", member.ToUpperInvariant());
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void ClearMember(string library, string name, string member)
    {
        EnsureMember(library, name, member);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM " + Quote(MemberTable(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant()));
        cmd.ExecuteNonQuery();
    }

    public void DeleteFile(string library, string name)
    {
        var members = ListMembers(library, name);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var member in members)
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE IF EXISTS " + Quote(MemberTable(library, name, member));
            drop.ExecuteNonQuery();
        }

        using (var deleteDef = connection.CreateCommand())
        {
            deleteDef.Transaction = transaction;
            deleteDef.CommandText = "DELETE FROM sys_file_defs WHERE lib = $lib AND name = $name AND type = '*FILE'";
            deleteDef.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
            deleteDef.Parameters.AddWithValue("$name", name.ToUpperInvariant());
            deleteDef.ExecuteNonQuery();
        }

        using (var deleteMembers = connection.CreateCommand())
        {
            deleteMembers.Transaction = transaction;
            deleteMembers.CommandText = "DELETE FROM sys_file_members WHERE lib = $lib AND name = $name AND type = '*FILE'";
            deleteMembers.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
            deleteMembers.Parameters.AddWithValue("$name", name.ToUpperInvariant());
            deleteMembers.ExecuteNonQuery();
        }

        transaction.Commit();
        _objects.Delete(library, name, FileType);
    }

    public void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var recordFormat = definition.Formats.First(f => string.Equals(f.Name, format, StringComparison.OrdinalIgnoreCase));
        var columns = new List<string>();
        var placeholders = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        foreach (var field in recordFormat.Fields)
        {
            columns.Add(Quote(field.Name));
            placeholders.Add("$v" + parameters.Count);
            values.TryGetValue(field.Name, out var raw);
            parameters.Add(("$v" + (parameters.Count), ToSqlValue(field, raw ?? DefaultFor(field))));
        }

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO " + Quote(MemberTable(library, name, member)) +
            " (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", placeholders) + ")";
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(string library, string name, string member)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var format = definition.PrimaryFormat;
        var columns = string.Join(", ", format.Fields.Select(f => Quote(f.Name)));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + columns + " FROM " + Quote(MemberTable(library, name, member)) + " ORDER BY _rowid_";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyed(string library, string name, string member)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var format = definition.PrimaryFormat;
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        var orderBy = keys.Count > 0
            ? " ORDER BY " + string.Join(", ", keys.Select(k => OrderKey(k)))
            : " ORDER BY _rowid_";

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) +
                " FROM " + Quote(MemberTable(library, name, member)) + orderBy;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefix(
        string library, string name, string member, IReadOnlyDictionary<string, object?> prefix)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var format = definition.PrimaryFormat;
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();

        if (keys.Count == 0)
        {
            throw new CpfException("CPF3201", $"File {name} has no key fields.");
        }

        var conditions = new List<string>();
        var parameters = 0;
        foreach (var key in keys)
        {
            if (!prefix.TryGetValue(key.Name, out _))
            {
                break;
            }

            var column = key.Type is FieldType.Zoned or FieldType.Packed && key.Decimals > 0
                ? "CAST(" + Quote(key.Name) + " AS REAL)"
                : Quote(key.Name);
            conditions.Add(column + " = $rk" + parameters++);
        }

        if (conditions.Count == 0)
        {
            throw new CpfException("CPF3201", $"No key values were supplied for file {name}.");
        }

        var orderBy = " ORDER BY " + string.Join(", ", keys.Select(k => OrderKey(k)));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) +
                " FROM " + Quote(MemberTable(library, name, member)) + " WHERE " + string.Join(" AND ", conditions) + orderBy;
            AddKeyParameters(cmd, format, keys, prefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    public void Update(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var recordFormat = definition.Formats.First(f => string.Equals(f.Name, format, StringComparison.OrdinalIgnoreCase));
        var keys = recordFormat.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        if (keys.Count == 0)
        {
            throw new CpfException("CPF3201", $"File {name} has no key fields.");
        }

        var setColumns = new List<string>();
        var whereColumns = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        foreach (var field in recordFormat.Fields)
        {
            if (!values.TryGetValue(field.Name, out var raw))
            {
                continue;
            }

            var placeholder = "$v" + parameters.Count;
            parameters.Add((placeholder, ToSqlValue(field, raw ?? DefaultFor(field))));
            if (field.Sequence > 0)
            {
                whereColumns.Add(Quote(field.Name) + " = " + placeholder);
            }
            else
            {
                setColumns.Add(Quote(field.Name) + " = " + placeholder);
            }
        }

        if (setColumns.Count == 0 || whereColumns.Count == 0)
        {
            return;
        }

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE " + Quote(MemberTable(library, name, member)) +
            " SET " + string.Join(", ", setColumns) + " WHERE " + string.Join(" AND ", whereColumns);
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    public void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues)
    {
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        var recordFormat = definition.Formats.First(f => string.Equals(f.Name, format, StringComparison.OrdinalIgnoreCase));
        var keys = recordFormat.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        if (keys.Count == 0)
        {
            throw new CpfException("CPF3201", $"File {name} has no key fields.");
        }

        var whereColumns = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        foreach (var key in keys)
        {
            if (!keyValues.TryGetValue(key.Name, out var raw))
            {
                break;
            }

            var placeholder = "$v" + parameters.Count;
            parameters.Add((placeholder, ToSqlValue(key, raw ?? DefaultFor(key))));
            whereColumns.Add(Quote(key.Name) + " = " + placeholder);
        }

        if (whereColumns.Count == 0)
        {
            throw new CpfException("CPF3201", $"No key values were supplied for file {name}.");
        }

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM " + Quote(MemberTable(library, name, member)) +
            " WHERE " + string.Join(" AND ", whereColumns);
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    public long RowCount(string library, string name, string member)
    {
        EnsureMember(library, name, member);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + Quote(MemberTable(library, name, member));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void CreateFileCore(string library, string name, string attribute, FileDefinition definition, string? source, string? description)
    {
        var libraryName = library.ToUpperInvariant();
        var fileName = name.ToUpperInvariant();

        if (_objects.Exists(libraryName, fileName, FileType))
        {
            throw new CpfException("CPF5805", $"File {fileName} already exists in library {libraryName}.");
        }

        _objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(libraryName, fileName),
            ObjectType = FileType,
            Attribute = attribute,
            Source = source,
            Description = description,
            Format = definition.PrimaryFormat.Name,
        });

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_file_defs (lib, name, type, def)
            VALUES ($lib, $name, '*FILE', $def)
            ON CONFLICT(lib, name, type) DO UPDATE SET def = excluded.def
            """;
        cmd.Parameters.AddWithValue("$lib", libraryName);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$def", definition.ToJson());
        cmd.ExecuteNonQuery();
    }

    private static FileDefinition SourceFormat(string name)
    {
        var definition = new FileDefinition { Name = name, Attribute = FileAttribute.Source };
        var format = new RecordFormat { Name = name.ToUpperInvariant() };
        format.Fields.Add(new FieldSpec { Name = "SRCSEQ", Type = FieldType.Packed, Length = 6, Decimals = 0 });
        format.Fields.Add(new FieldSpec { Name = "SRCDAT", Type = FieldType.Date, Length = 8 });
        format.Fields.Add(new FieldSpec
        {
            Name = "SRCDTA",
            Type = FieldType.Alpha,
            Length = 100,
            Ccsid = 37,
            VariableLength = true,
        });
        definition.Formats.Add(format);
        format.AssignPositions();
        return definition;
    }

    private static string CreateMemberTableSql(RecordFormat format, string tableName)
    {
        var columns = new List<string>();
        foreach (var field in format.Fields)
        {
            columns.Add(Quote(field.Name) + " " + SqliteType(field) + " NULL");
        }

        return "CREATE TABLE IF NOT EXISTS " + Quote(tableName) + " (" + string.Join(", ", columns) + ")";
    }

    private static string SqliteType(FieldSpec field) => field.Type switch
    {
        FieldType.Alpha or FieldType.Date or FieldType.Time or FieldType.Timestamp => "TEXT",
        FieldType.Zoned or FieldType.Packed => field.Decimals == 0 ? "INTEGER" : "TEXT",
        FieldType.Binary => "INTEGER",
        FieldType.Float => "REAL",
        FieldType.Logic => "INTEGER",
        _ => "TEXT",
    };

    private static object? ToSqlValue(FieldSpec field, object? value)
    {
        var normalized = value ?? DefaultFor(field);

        return field.Type switch
        {
            FieldType.Alpha => normalized as string ?? ToString(normalized),
            FieldType.Zoned or FieldType.Packed => field.Decimals == 0
                ? ToDecimal(normalized)
                : ToDecimal(normalized).ToString("F" + field.Decimals, CultureInfo.InvariantCulture),
            FieldType.Binary => ToLong(normalized),
            FieldType.Float => ToDouble(normalized),
            FieldType.Logic => ToBool(normalized) ? 1L : 0L,
            FieldType.Date => ToDate(normalized).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Time => ToTime(normalized).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            FieldType.Timestamp => ToDate(normalized).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => normalized as string ?? ToString(normalized),
        };
    }

    private static IReadOnlyDictionary<string, object?> ReadRow(System.Data.Common.DbDataReader reader, RecordFormat format)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var field = format.Fields[index];
            values[field.Name] = reader.IsDBNull(index) ? null : FromSqlValue(field, reader.GetValue(index));
        }

        return values;
    }

    private static object? FromSqlValue(FieldSpec field, object raw) => field.Type switch
    {
        FieldType.Alpha => raw as string ?? string.Empty,
        FieldType.Zoned or FieldType.Packed => decimal.TryParse(raw as string ?? ToString(raw),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
        FieldType.Binary => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
        FieldType.Float => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
        FieldType.Logic => Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0,
        FieldType.Date => DateTimeOffset.TryParse(raw as string, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue,
        FieldType.Time => TimeSpan.TryParse(raw as string, CultureInfo.InvariantCulture, out var time) ? time : TimeSpan.Zero,
        FieldType.Timestamp => DateTimeOffset.TryParse(raw as string, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : DateTimeOffset.MinValue,
        _ => raw as string ?? string.Empty,
    };

    private void EnsureMember(string library, string name, string member)
    {
        if (!MemberExists(library, name, member))
        {
            throw new CpfException("CPF2817", $"Member {member} not found in file {name}.");
        }
    }

    private void AddKeyParameters(Microsoft.Data.Sqlite.SqliteCommand cmd, RecordFormat format,
        IReadOnlyList<FieldSpec> keys, IReadOnlyDictionary<string, object?> prefix)
    {
        var parameters = 0;
        foreach (var key in keys)
        {
            if (!prefix.TryGetValue(key.Name, out var value))
            {
                break;
            }

            cmd.Parameters.AddWithValue("$rk" + parameters++, ToSqlValue(key, value));
        }
    }

    private static string OrderKey(FieldSpec key)
    {
        var column = key.Type is FieldType.Zoned or FieldType.Packed && key.Decimals > 0
            ? "CAST(" + Quote(key.Name) + " AS REAL)"
            : Quote(key.Name);
        return column + (key.Descending ? " DESC" : string.Empty);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static object DefaultFor(FieldSpec field) => field.Type switch
    {
        FieldType.Alpha => string.Empty,
        FieldType.Logic => false,
        FieldType.Binary => 0L,
        FieldType.Float => 0d,
        FieldType.Date => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        FieldType.Time => TimeSpan.Zero,
        FieldType.Timestamp => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        _ => 0m,
    };

    private static string ToString(object value) => value?.ToString() ?? string.Empty;

    private static decimal ToDecimal(object value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        double f => System.Convert.ToDecimal(f),
        _ => decimal.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m,
    };

    private static long ToLong(object value) => value switch
    {
        long l => l,
        int i => i,
        decimal d => decimal.ToInt64(d),
        _ => long.TryParse(ToString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L,
    };

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        decimal m => (double)m,
        long l => l,
        _ => double.TryParse(ToString(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d,
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        _ => ToString(value) is "1" or "Y" or "T" or "true",
    };

    private static DateTimeOffset ToDate(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(d, TimeSpan.Zero),
        _ => DateTimeOffset.TryParse(ToString(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue,
    };

    private static TimeSpan ToTime(object value) => value switch
    {
        TimeSpan t => t,
        DateTimeOffset d => d.TimeOfDay,
        _ => TimeSpan.TryParse(ToString(value), CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero,
    };
}

public static class DbFormatting
{
    public static string FormatRecord(RecordFormat format, IReadOnlyDictionary<string, object?> values)
    {
        var row = new List<string>();
        foreach (var field in format.Fields)
        {
            values.TryGetValue(field.Name, out var value);
            row.Add(RecordFormatting.ToText(field, value).TrimEnd());
        }

        return string.Join(" | ", row);
    }

    public static string ColumnHeader(RecordFormat format)
    {
        var widths = format.Fields.Select(f => Math.Max(f.Name.Length, DisplayWidth(f))).ToList();
        var header = new List<string>();
        var ruler = new List<string>();
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var width = widths[index];
            header.Add(format.Fields[index].Name.PadRight(width));
            ruler.Add(new string('-', width));
        }

        return string.Join(" | ", header) + Environment.NewLine + string.Join("-+-", ruler);
    }

    public static IReadOnlyList<string> FormatColumn(RecordFormat format, IReadOnlyDictionary<string, object?> values)
    {
        var widths = format.Fields.Select(f => Math.Max(f.Name.Length, DisplayWidth(f))).ToList();
        var cells = new List<string>();
        for (var index = 0; index < format.Fields.Count; index++)
        {
            values.TryGetValue(format.Fields[index].Name, out var value);
            cells.Add(RecordFormatting.ToText(format.Fields[index], value).PadRight(widths[index]));
        }

        return cells;
    }

    private static int DisplayWidth(FieldSpec field) => field.Type switch
    {
        FieldType.Alpha => field.Length,
        FieldType.Zoned or FieldType.Packed => field.Length + 1,
        FieldType.Binary => 12,
        FieldType.Float => 22,
        FieldType.Logic => 1,
        FieldType.Date => 10,
        FieldType.Time => 8,
        FieldType.Timestamp => 19,
        _ => field.Length,
    };
}