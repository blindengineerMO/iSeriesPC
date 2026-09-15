using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private const string FileType = "*FILE";
    private const string SourceAttribute = "*SRCPF";
    private const string PhysicalAttribute = "*PF";

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteObjectStore _objects;
    private readonly string _owner;

    public SqliteFileStore(SqliteConnectionFactory factory, IObjectStore objects, string owner = "QSECOFR")
    {
        _factory = factory;
        _owner = owner;
        _objects = objects as SqliteObjectStore ?? throw new ArgumentException("SQLite file storage requires a SQLite object catalog.", nameof(objects));
    }

    public static string MemberTable(string library, string name, string member) =>
        (library + "." + name + "." + member).ToUpperInvariant();

    public bool FileExists(string library, string name) =>
        _objects.Exists(library, name, FileType);

    public FileDefinition? GetDefinition(string library, string name)
    {
        Authorize(library, name, AuthorityBit.ObjectOperate);
        using var connection = _factory.Open();
        return ReadDefinition(connection, null, library, name);
    }
    private static FileDefinition? ReadDefinition(SqliteConnection connection, SqliteTransaction? transaction, string library, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT def FROM sys_file_defs WHERE lib = $lib AND name = $name AND type = '*FILE'";
        cmd.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : FileDefinition.FromJson(json);
    }

    public void CreatePhysicalFile(string library, string name, FileDefinition definition, string source, string? description = null, string member = "*FILE", int? maximumMembers = null)
    {
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null)
            throw new CpfException("IPC0138", "Physical file creation requires a physical definition.");
        if (maximumMembers is { } maximum) definition.MaximumMembers = maximum;
        if (definition.MaximumMembers is < 1 or > 32767 || definition.Unique && !definition.PrimaryFormat.Fields.Any(f => f.Sequence > 0) || !definition.Unique && definition.ExcludeNullKeys)
            throw LogicalError("Invalid physical member limit or key uniqueness definition.");
        CreateFileCore(library, name, PhysicalAttribute, definition, source, description, member);
    }

    public void CreateSourceFile(string library, string name, string? description = null, int sourceWidth = 100)
    {
        if (sourceWidth is < 44 or > 5000) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        var definition = SourceFormat(name, sourceWidth);
        CreateFileCore(library, name, SourceAttribute, definition, null, description);
    }

    public bool MemberExists(string library, string name, string member)
    {
        Authorize(library, name, AuthorityBit.ObjectOperate);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib = $lib AND name = $name AND type = '*FILE' AND mbr = $mbr";
        cmd.Parameters.AddWithValue("$lib", library.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$mbr", member.ToUpperInvariant());
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public string FirstMember(string library, string name)
    {
        Authorize(library, name, AuthorityBit.ObjectOperate);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
        command.Parameters.AddWithValue("$lib", library.ToUpperInvariant()); command.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        return command.ExecuteScalar() as string ?? throw new CpfException("CPF2817", "File has no members.");
    }

    public IReadOnlyList<string> ListMembers(string library, string name)
    {
        Authorize(library, name, AuthorityBit.ObjectOperate);
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
        Authorize(library, name, AuthorityBit.ObjectManagement | AuthorityBit.ObjectOperate);
        var libraryName = library.ToUpperInvariant();
        var fileName = name.ToUpperInvariant();
        var memberName = member.ToUpperInvariant();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // Resolve the definition after taking the writer transaction. Concurrent
        // member DDL must not race an earlier schema read on another connection.
        var definition = ReadDefinition(connection, transaction, libraryName, fileName)
            ?? throw new CpfException("CPF9801", $"File {fileName} in library {libraryName} not found.");

        if (definition.Logical is not null) { transaction.Rollback(); AddLogicalMember(libraryName, fileName, memberName); return; }
        CheckNewPhysicalMember(connection, transaction, libraryName, fileName, memberName, definition);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sys_file_members (lib, name, type, mbr, created)
                VALUES ($lib, $name, '*FILE', $mbr, $created)
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
            create.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(libraryName, fileName, memberName)).Replace("IF NOT EXISTS ", "", StringComparison.Ordinal);
            create.ExecuteNonQuery();
        }

        CreatePhysicalAccessPath(connection, transaction, libraryName, fileName, memberName, definition);
        using var touch = connection.CreateCommand(); touch.Transaction = transaction;
        touch.CommandText = "UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
        touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); touch.Parameters.AddWithValue("$lib", libraryName); touch.Parameters.AddWithValue("$name", fileName);
        touch.ExecuteNonQuery(); transaction.Commit();
    }

    public void RemoveMember(string library, string name, string member) =>
        new ObjectCatalogOperations(_factory).RemoveFileMember(new QualifiedName(library.ToUpperInvariant(), name.ToUpperInvariant()), member.ToUpperInvariant());

    public void ClearMember(string library, string name, string member)
    {
        Authorize(library, name, AuthorityBit.Delete);
        new Ipc.Services.Work.JobLockStore(_factory).RequireObjectMutation(library.ToUpperInvariant(), name.ToUpperInvariant(), FileType);
        EnsureMember(library, name, member);
        if (GetDefinition(library, name)?.Logical is not null) throw LogicalError("CLRPFM requires a physical file.");
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM " + Quote(MemberTable(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant()));
        cmd.ExecuteNonQuery();
    }

    public void DeleteFile(string library, string name)
    {
        new ObjectCatalogOperations(_factory).Delete(new QualifiedName(library.ToUpperInvariant(), name.ToUpperInvariant()), FileType);
    }

    public void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values)
    {
        Authorize(library, name, AuthorityBit.Add);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) { MutateLogical(definition, member, format, values, "INSERT"); return; }
        var recordFormat = definition.Formats.First(f => string.Equals(f.Name, format, StringComparison.OrdinalIgnoreCase));
        var columns = new List<string>();
        var placeholders = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        foreach (var field in recordFormat.Fields)
        {
            columns.Add(Quote(field.Name));
            placeholders.Add("$v" + parameters.Count);
            if (!values.TryGetValue(field.Name, out var raw)) raw = DefaultFor(field);
            if (values.ContainsKey(field.Name) && raw is null && !field.NullCapable) throw LogicalError("Explicit null is not allowed for field " + field.Name + ".");
            parameters.Add(("$v" + (parameters.Count), RecordSqlValue(field, raw)));
        }

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        EnsureExactDecimalStorage(connection, transaction, MemberTable(library, name, member), recordFormat);
        cmd.CommandText = "INSERT INTO " + Quote(MemberTable(library, name, member)) +
            " (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", placeholders) + ")";
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT last_insert_rowid()";
        new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), Convert.ToInt64(cmd.ExecuteScalar()), write: true);
        transaction.Commit();
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAllCore(string library, string name, string member)
    {
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: false);
        var format = definition.PrimaryFormat;
        var columns = string.Join(", ", format.Fields.Select(f => Quote(f.Name)));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + columns + "," + RowIdentitySql(format) + " FROM " + Quote(MemberTable(library, name, member)) + " ORDER BY _rowid_";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), reader.GetInt64(format.Fields.Count), write: false);
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyedCore(string library, string name, string member)
    {
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: true);
        var format = definition.PrimaryFormat;
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        var orderBy = keys.Count > 0
            ? " ORDER BY " + string.Join(", ", keys.Select(k => OrderKey(k)))
            : " ORDER BY _rowid_";

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var connection = _factory.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) + "," + RowIdentitySql(format) +
                " FROM " + Quote(MemberTable(library, name, member)) + orderBy;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), reader.GetInt64(format.Fields.Count), write: false);
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefixCore(
        string library, string name, string member, IReadOnlyDictionary<string, object?> prefix)
    {
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: true, prefix);
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

            var parameter = "$rk" + parameters++;
            conditions.Add(LogicalColumn(key) + " IS " + LogicalExpression(key, parameter));
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
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) + "," + RowIdentitySql(format) +
                " FROM " + Quote(MemberTable(library, name, member)) + " WHERE " + string.Join(" AND ", conditions) + orderBy;
            AddKeyParameters(cmd, format, keys, prefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), reader.GetInt64(format.Fields.Count), write: false);
                rows.Add(ReadRow(reader, format));
            }
        }

        return rows;
    }

    public void Update(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values)
    {
        Authorize(library, name, AuthorityBit.Update);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) { MutateLogical(definition, member, format, values, "UPDATE"); return; }
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
            if (raw is null && !field.NullCapable) throw LogicalError("Explicit null is not allowed for field " + field.Name + ".");
            parameters.Add((placeholder, RecordSqlValue(field, raw)));
            if (field.Sequence > 0)
            {
                whereColumns.Add(LogicalColumn(field) + " IS " + LogicalExpression(field, placeholder));
            }
            else
            {
                setColumns.Add(Quote(field.Name) + " = " + placeholder);
            }
        }

        if (whereColumns.Count != keys.Count) throw new CpfException("CPF3201", "Update requires a complete key.");
        if (setColumns.Count == 0)
        {
            return;
        }

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        EnsureExactDecimalStorage(connection, transaction, MemberTable(library, name, member), recordFormat);
        cmd.CommandText = "UPDATE " + Quote(MemberTable(library, name, member)) +
            " SET " + string.Join(", ", setColumns) + " WHERE " + string.Join(" AND ", whereColumns);
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        var mutation = cmd.CommandText;
        cmd.CommandText = "SELECT " + RowIdentitySql(recordFormat) + " FROM " + Quote(MemberTable(library, name, member)) + " WHERE " + string.Join(" AND ", whereColumns);
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
            new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), reader.GetInt64(0), write: true);
        cmd.CommandText = mutation; cmd.ExecuteNonQuery(); transaction.Commit();
    }

    public void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues)
    {
        Authorize(library, name, AuthorityBit.Delete);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.Logical is not null) { MutateLogical(definition, member, format, keyValues, "DELETE"); return; }
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
            if (raw is null && !key.NullCapable) throw LogicalError("Null is not allowed for key " + key.Name + ".");
            parameters.Add((placeholder, RecordSqlValue(key, raw)));
            whereColumns.Add(LogicalColumn(key) + " IS " + LogicalExpression(key, placeholder));
        }

        if (whereColumns.Count != keys.Count)
        {
            throw new CpfException("CPF3201", $"No key values were supplied for file {name}.");
        }

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM " + Quote(MemberTable(library, name, member)) +
            " WHERE " + string.Join(" AND ", whereColumns);
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        var mutation = cmd.CommandText;
        cmd.CommandText = "SELECT " + RowIdentitySql(recordFormat) + " FROM " + Quote(MemberTable(library, name, member)) + " WHERE " + string.Join(" AND ", whereColumns);
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
            new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), reader.GetInt64(0), write: true);
        cmd.CommandText = mutation; cmd.ExecuteNonQuery(); transaction.Commit();
    }

    public long RowCount(string library, string name, string member)
    {
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        if (GetDefinition(library, name) is { Logical: not null } logical) return ReadLogical(library, name, member, logical, keyed: false).Count;
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + Quote(MemberTable(library, name, member));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Authorize(string library, string name, AuthorityBit permission)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(library.ToUpperInvariant(), name.ToUpperInvariant(), FileType, permission | AuthorityBit.ObjectOperate);
        AuthorizeLogicalSource(library, name, permission);
    }

    private void CreateFileCore(string library, string name, string attribute, FileDefinition definition, string? source, string? description, string member = "*FILE")
    {
        var libraryName = library.ToUpperInvariant();
        var fileName = name.ToUpperInvariant();
        member = member.ToUpperInvariant(); if (member == "*FILE") member = fileName;
        if (member != "*NONE" && !ObjectName.IsValid(member)) throw LogicalError("Invalid physical member name.");

        if (_objects.Exists(libraryName, fileName, FileType))
        {
            throw new CpfException("CPF5805", $"File {fileName} already exists in library {libraryName}.");
        }

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        _objects.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(libraryName, fileName),
            Owner = _owner,
            ObjectType = FileType, Attribute = attribute, Source = source, Ccsid = definition.Ccsid,
            Description = description, Format = definition.PrimaryFormat.Name,
        }, connection, transaction);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_file_defs (lib, name, type, def) VALUES ($lib, $name, '*FILE', $def);
            INSERT INTO sys_file_members (lib, name, type, mbr, created) SELECT $lib, $name, '*FILE', $member, $now WHERE $member <> '*NONE';
            """;
        cmd.Parameters.AddWithValue("$lib", libraryName);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$def", definition.ToJson());
        cmd.Parameters.AddWithValue("$member", member);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        if (member != "*NONE")
        {
            cmd.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(libraryName, fileName, member))
                .Replace("IF NOT EXISTS ", "", StringComparison.Ordinal);
            cmd.ExecuteNonQuery();
            CreatePhysicalAccessPath(connection, transaction, libraryName, fileName, member, definition);
        }
        transaction.Commit();
    }

    private static FileDefinition SourceFormat(string name, int sourceWidth)
    {
        var definition = new FileDefinition { Name = name, Attribute = FileAttribute.Source };
        var format = new RecordFormat { Name = name.ToUpperInvariant() };
        format.Fields.Add(new FieldSpec { Name = "SRCSEQ", Type = FieldType.Packed, Length = 6, Decimals = 0 });
        format.Fields.Add(new FieldSpec { Name = "SRCDAT", Type = FieldType.Date, Length = 8 });
        format.Fields.Add(new FieldSpec
        {
            Name = "SRCDTA",
            Type = FieldType.Alpha,
            Length = sourceWidth,
            Ccsid = 37,
            VariableLength = true,
        });
        definition.Formats.Add(format);
        format.AssignPositions();
        return definition;
    }

    private static string RowIdentitySql(RecordFormat format) =>
        new[] { "_rowid_", "rowid", "oid" }.FirstOrDefault(alias => format.Fields.All(f => !f.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
        ?? throw new CpfException("IPC0125", "File hides all SQLite row identities; record locking is unavailable.");

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
        FieldType.Zoned or FieldType.Packed => field.Decimals == 0 && field.Length <= 18 ? "INTEGER" : "TEXT",
        FieldType.Binary => "INTEGER",
        FieldType.Float => "REAL",
        FieldType.Logic => "INTEGER",
        _ => "TEXT",
    };

    private static object? ToSqlValue(FieldSpec field, object? value)
    {
        var normalized = value ?? DefaultFor(field);
        if (normalized is null) return null;

        return field.Type switch
        {
            FieldType.Alpha => normalized as string ?? ToString(normalized),
            FieldType.Zoned or FieldType.Packed => field.Decimals == 0
                ? ToDecimal(normalized)
                : ToDecimal(normalized).ToString("F" + field.Decimals, CultureInfo.InvariantCulture),
            FieldType.Binary => ToLong(normalized),
            FieldType.Float => field.Length == 4 ? (double)(float)ToDouble(normalized) : ToDouble(normalized),
            FieldType.Logic => ToBool(normalized) ? 1L : 0L,
            FieldType.Date => ToDate(normalized).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Time => ToTime(normalized).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            FieldType.Timestamp => ToDate(normalized).ToString(field.Length == 26 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => normalized as string ?? ToString(normalized),
        };
    }

    private static IReadOnlyDictionary<string, object?> ReadRow(System.Data.Common.DbDataReader reader, RecordFormat format)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < format.Fields.Count; index++)
        {
            var field = format.Fields[index];
            if (reader.IsDBNull(index) && !field.NullCapable) throw LogicalError("Null stored in a non-null field.");
            if (!reader.IsDBNull(index))
            {
                if (field.Type is FieldType.Packed or FieldType.Zoned && reader.GetValue(index) is double) throw LogicalError("Decimal stored with floating-point affinity; restore its exact value before reading.");
                _ = RecordSqlValue(field, reader.GetValue(index));
            }
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
        FieldType.Float => field.Length == 4 ? (object)(float)Convert.ToDouble(raw, CultureInfo.InvariantCulture) : Convert.ToDouble(raw, CultureInfo.InvariantCulture),
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

            if (value is null && !key.NullCapable) throw LogicalError("Null is not allowed for key " + key.Name + ".");
            cmd.Parameters.AddWithValue("$rk" + parameters++, RecordSqlValue(key, value) ?? DBNull.Value);
        }
    }

    private static string OrderKey(FieldSpec key)
    {
        var column = LogicalColumn(key);
        return DatabaseSortKeys.KeyColumns(column, key.Descending, nullable: key.NullCapable);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static object? DefaultFor(FieldSpec field)
    {
        if (field.DefaultValue is { } explicitDefault) return explicitDefault.Value;
        if (field.CurrentDatetimeDefault)
        {
            var now = DateTimeOffset.UtcNow;
            return field.Type switch {
                FieldType.Date => new DateTimeOffset(now.Date, TimeSpan.Zero),
                FieldType.Time => TimeSpan.FromTicks(now.TimeOfDay.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
                FieldType.Timestamp => now.AddTicks(-(now.Ticks % 10)),
                _ => throw LogicalError("Invalid current-datetime default."),
            };
        }
        if (field.NullCapable) return null;
        return field.Type switch {
        FieldType.Alpha => string.Empty,
        FieldType.Logic => false,
        FieldType.Binary => 0L,
        FieldType.Float => 0d,
        FieldType.Date => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        FieldType.Time => TimeSpan.Zero,
        FieldType.Timestamp => new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        _ => 0m,
        };
    }

    private static string ToString(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero),
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => DateTimeOffset.TryParse(ToString(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue,
    };

    private static TimeSpan ToTime(object value) => value switch
    {
        TimeSpan t => t,
        TimeOnly t => t.ToTimeSpan(),
        DateTime d => d.TimeOfDay,
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
