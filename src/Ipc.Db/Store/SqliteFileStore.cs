using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Records;
using Ipc.Db.Sql;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
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
        _objects.Exists(ResolveLibrary(library), name.ToUpperInvariant(), FileType);

    public FileDefinition? GetDefinition(string library, string name)
    {
        library = ResolveLibrary(library);
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

    public void CreatePhysicalFile(string library, string name, FileDefinition definition, string source, string? description = null, string member = "*FILE", int? maximumMembers = null, CancellationToken cancellationToken = default)
    {
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null || definition.SqlView is not null)
            throw new CpfException("IPC0138", "Physical file creation requires a physical definition.");
        if (maximumMembers is { } maximum) definition.MaximumMembers = maximum;
        definition.MemberSize?.Validate();
        if (definition.RecordLockWaitSeconds is < 0 or > 32767 || definition.MaximumMembers is < 1 or > 32767 || definition.Unique && !definition.PrimaryFormat.Fields.Any(f => f.Sequence > 0) || !definition.Unique && definition.ExcludeNullKeys)
            throw LogicalError("Invalid physical member limit or key uniqueness definition.");
        CreateFileCore(library, name, PhysicalAttribute, definition, source, description, member, cancellationToken);
    }

    public void CreateSourceFile(string library, string name, string? description = null, int sourceWidth = 100, FileMemberSize? memberSize = null)
    {
        if (sourceWidth is < 44 or > 5000) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        memberSize?.Validate();
        var definition = SourceFormat(name, sourceWidth);
        definition.MemberSize = memberSize;
        CreateFileCore(library, name, SourceAttribute, definition, null, description);
    }

    public bool MemberExists(string library, string name, string member)
    {
        library = ResolveLibrary(library);
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
        library = ResolveLibrary(library);
        Authorize(library, name, AuthorityBit.ObjectOperate);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr LIMIT 1";
        command.Parameters.AddWithValue("$lib", library.ToUpperInvariant()); command.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        return command.ExecuteScalar() as string ?? throw new CpfException("CPF2817", "File has no members.");
    }

    public IReadOnlyList<string> ListMembers(string library, string name)
    {
        library = ResolveLibrary(library);
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
        library = ResolveLibrary(library);
        AuthorizeFileManagement(library.ToUpperInvariant(), name.ToUpperInvariant());
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject("QSYS", library.ToUpperInvariant(), Ipc.Core.Objects.ObjectType.Library,
            AuthorityBit.ObjectOperate | AuthorityBit.Add, checkLibrary: false);
        var libraryName = library.ToUpperInvariant();
        var fileName = name.ToUpperInvariant();
        var memberName = member.ToUpperInvariant();
        using var establishment = new ConstraintAllocations();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // Resolve the definition after taking the writer transaction. Concurrent
        // member DDL must not race an earlier schema read on another connection.
        var definition = ReadDefinition(connection, transaction, libraryName, fileName)
            ?? throw new CpfException("CPF9801", $"File {fileName} in library {libraryName} not found.");

        if (definition.Logical is not null) { transaction.Rollback(); AddLogicalMember(libraryName, fileName, memberName); return; }
        CheckNewPhysicalMember(connection, transaction, libraryName, fileName, memberName, definition);
        ValidateSqlIndexMemberAddition(connection, transaction, libraryName, fileName);
        RequireEstablishmentAvailable(connection, transaction, new(libraryName, fileName), definition, _factory.ShutdownToken);
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
            create.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(libraryName, fileName, memberName), definition.SqlTable).Replace("IF NOT EXISTS ", "", StringComparison.Ordinal);
            create.ExecuteNonQuery();
        }

        CreatePhysicalAccessPath(connection, transaction, libraryName, fileName, memberName, definition);
        CreateSqlConstraints(connection, transaction, MemberTable(libraryName, fileName, memberName), definition);
        JournalHooks.InstallMember(connection, transaction, libraryName, fileName, memberName);
        AddSqlIndexMember(connection, transaction, libraryName, fileName, memberName, definition);
        using var touch = connection.CreateCommand(); touch.Transaction = transaction;
        touch.CommandText = "UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
        touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); touch.Parameters.AddWithValue("$lib", libraryName); touch.Parameters.AddWithValue("$name", fileName);
        touch.ExecuteNonQuery();
        EstablishDefinedForeignKeys(connection, transaction, new(libraryName, fileName), establishment, _factory.ShutdownToken);
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, libraryName, fileName,
            AuthorityBit.ObjectOperate, alternatives: AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter);
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireBoundObject(connection, transaction, "QSYS", libraryName,
            AuthorityBit.ObjectOperate | AuthorityBit.Add, objectType: ObjectType.Library, checkLibrary: false);
        transaction.Commit();
    }

    public void RemoveMember(string library, string name, string member) =>
        new ObjectCatalogOperations(_factory).RemoveFileMember(new QualifiedName(library.ToUpperInvariant(), name.ToUpperInvariant()), member.ToUpperInvariant());

    public void ClearMember(string library, string name, string member, CancellationToken cancellationToken = default)
        => ClearMemberCore(library, name, member, cancellationToken);

    private void ClearMemberCore(string library, string name, string member, CancellationToken cancellationToken,
        Action? beforeWrite = null, Action<SqliteConnection, SqliteTransaction>? beforePublish = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _factory.ShutdownToken);
        cancellationToken = lifetime.Token;
        using var recordLocks = new JobLockStore(_factory).EnterRecordWrite(cancellationToken);
        library = ResolveLibrary(library).ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested(); RequireUnenlisted(library, name, "CLRPFM");
        AuthorizeFileManagement(library, name, AuthorityBit.ObjectOperate | AuthorityBit.Delete);
        new JobLockStore(_factory).RequireObjectMutation(library, name, FileType);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, cancellationToken);
        if (Ipc.Services.Events.OperationIdentity.Current?.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
        AuthorizeFileManagement(library, name, AuthorityBit.ObjectOperate | AuthorityBit.Delete); RequireUnenlisted(library, name, "CLRPFM");
        beforeWrite?.Invoke();
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("CPF9801", "Physical file not found.");
        if (definition.Logical is not null) throw LogicalError("CLRPFM requires a physical file.");
        var id = RecordSlots.Identity(connection, transaction, library, name, member) ?? throw new CpfException("CPF2817", "Member not found.");
        RequireReferentialClear(connection,transaction,new(library,name),member);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM " + Quote(MemberTable(library, name, member));
        SqliteCancellation.Run(connection, cancellationToken, cmd.ExecuteNonQuery);
        MemberCapacity.Reset(connection, transaction, id);
        RecordSlots.Reset(connection, transaction, id);
        beforePublish?.Invoke(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public void DeleteFile(string library, string name)
    {
        new ObjectCatalogOperations(_factory).Delete(new QualifiedName(library.ToUpperInvariant(), name.ToUpperInvariant()), FileType);
    }

    public void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        => WithCapacityRecovery(token => { InsertCore(library, name, member, format, values, token); return true; }, cancellationToken,
            request => ValidateCapacityTarget(library,name,member,request));
    private void InsertCore(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken, Action? validateAfterWait = null, long? relativeNumber = null,
        Action<SqliteConnection, SqliteTransaction>? beforePublish = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        library = ResolveLibrary(library);
        Authorize(library, name, AuthorityBit.Add);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.SqlView is not null) throw LogicalError("SQL view record output requires the remaining view update implementation.");
        if (definition.Logical is not null) { MutateLogical(definition, member, format, values, "INSERT", cancellationToken, validateAfterWait, relativeNumber); return; }
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

        using var data = OpenRecordData(library, name, write: true, cancellationToken, members: [member]);
        validateAfterWait?.Invoke();
        if (relativeNumber is not null && data.IsWorkspace) throw LogicalError("Relative record output is not supported in a commitment workspace.");
        var connection = data.Connection; var transaction = data.Transaction!;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        EnsureExactDecimalStorage(connection, transaction, MemberTable(library, name, member), recordFormat);
        if (!data.IsWorkspace)
        {
            var identity = RowIdentitySql(recordFormat);
            var number = relativeNumber ?? RecordSlots.Next(connection, transaction, library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), identity, definition.ReuseDeletedRecords);
            if (relativeNumber is not null) RequireRelativeOutputSlot(connection, transaction, library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), identity, number, cancellationToken);
            else RequireMemberCapacity(connection, transaction, library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), definition, number);
            new JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), number, write: true);
            columns.Insert(0, Quote(identity)); placeholders.Insert(0, "$rrn"); parameters.Add(("$rrn",number));
        }
        cmd.CommandText = "INSERT INTO " + Quote(MemberTable(library, name, member)) +
            " (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", placeholders) + ")";
        foreach (var (parameterName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        data.Prepare(cmd); Ipc.Services.Sqlite.SqliteCancellation.Run(connection, cancellationToken, cmd.ExecuteNonQuery);
        cmd.CommandText = "SELECT last_insert_rowid()";
        new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), Convert.ToInt64(cmd.ExecuteScalar()), write: true);
        cancellationToken.ThrowIfCancellationRequested(); beforePublish?.Invoke(connection, transaction); data.Complete();
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAllCore(string library, string name, string member)
    {
        library = ResolveLibrary(library);
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.SqlView is not null) return ReadSqlViewRows(library, name);
        if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: false);
        var format = definition.PrimaryFormat;
        var columns = string.Join(", ", format.Fields.Select(f => Quote(f.Name)));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var data = OpenRecordData(library, name, members: [member]))
        {
            var connection = data.Connection;
            using var cmd = connection.CreateCommand(); cmd.Transaction = data.Transaction;
            cmd.CommandText = "SELECT " + columns + "," + RowIdentitySql(format) + " FROM " + Quote(MemberTable(library, name, member)) + " ORDER BY _rowid_";
            data.Prepare(cmd); using var reader = cmd.ExecuteReader();
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
        library = ResolveLibrary(library);
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        var definition = GetDefinition(library, name)!;
        if (definition.SqlView is not null) return ReadSqlViewRows(library, name);
        if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: true);
        var format = definition.PrimaryFormat;
        var keys = format.Fields.Where(f => f.Sequence > 0).OrderBy(f => f.Sequence).ToList();
        var orderBy = keys.Count > 0
            ? " ORDER BY " + string.Join(", ", keys.Select(k => OrderKey(k)))
            : " ORDER BY _rowid_";

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var data = OpenRecordData(library, name, members: [member]))
        {
            var connection = data.Connection;
            using var cmd = connection.CreateCommand(); cmd.Transaction = data.Transaction;
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) + "," + RowIdentitySql(format) +
                " FROM " + Quote(MemberTable(library, name, member)) + orderBy;
            data.Prepare(cmd); using var reader = cmd.ExecuteReader();
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
        library = ResolveLibrary(library);
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
        using (var data = OpenRecordData(library, name, members: [member]))
        {
            var connection = data.Connection;
            using var cmd = connection.CreateCommand(); cmd.Transaction = data.Transaction;
            cmd.CommandText = "SELECT " + string.Join(", ", format.Fields.Select(f => Quote(f.Name))) + "," + RowIdentitySql(format) +
                " FROM " + Quote(MemberTable(library, name, member)) + " WHERE " + string.Join(" AND ", conditions) + orderBy;
            AddKeyParameters(cmd, format, keys, prefix);
            data.Prepare(cmd); using var reader = cmd.ExecuteReader();
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
        library = ResolveLibrary(library);
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

        using var data = OpenRecordData(library, name, write: true, members: [member]);
        var connection = data.Connection; var transaction = data.Transaction!;
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
        data.Prepare(cmd);
        var selectedRows = new HashSet<long>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        {
            var number = reader.GetInt64(0); selectedRows.Add(number);
            new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), number, write: true);
        }
        if (!data.IsWorkspace) data.AddCascadeLocks(LockForeignParentChecks(connection,transaction,new(library.ToUpperInvariant(),name.ToUpperInvariant()),member.ToUpperInvariant(),selectedRows,
            recordFormat.Fields.Where(field=>field.Sequence==0 && values.ContainsKey(field.Name)).Select(field=>field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),CancellationToken.None));
        cmd.CommandText = mutation; data.Prepare(cmd); cmd.ExecuteNonQuery(); data.Complete();
    }

    public void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues)
    {
        library = ResolveLibrary(library);
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

        using var data = OpenRecordData(library, name, write: true, members: [member]);
        var connection = data.Connection; var transaction = data.Transaction!;
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
        data.Prepare(cmd);
        var deletedRows = new HashSet<long>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        {
            var number=reader.GetInt64(0); deletedRows.Add(number);
            new Ipc.Services.Work.JobLockStore(_factory).RequireRecordAccess(library.ToUpperInvariant(), name.ToUpperInvariant(), member.ToUpperInvariant(), number, write: true);
        }
        if (!data.IsWorkspace) data.AddCascadeLocks(LockForeignCascades(connection,transaction,new(library.ToUpperInvariant(),name.ToUpperInvariant()),member.ToUpperInvariant(),deletedRows,CancellationToken.None));
        cmd.CommandText = mutation; data.Prepare(cmd); cmd.ExecuteNonQuery(); data.Complete();
    }

    public long RowCount(string library, string name, string member)
    {
        library = ResolveLibrary(library);
        Authorize(library, name, AuthorityBit.Read);
        EnsureMember(library, name, member);
        if (GetDefinition(library, name) is { } definition)
        {
            if (definition.SqlView is not null)
            {
                using var query = OpenSqlQuery("SELECT COUNT_BIG(*) AS N FROM " + library + "/" + name, ViewReadOptions(library));
                return query.Read() ? (long)query.Current![0]! : 0;
            }
            if (definition.Logical is not null) return ReadLogical(library, name, member, definition, keyed: false).Count;
        }
        using var data = OpenRecordData(library, name, members: [member]);
        var connection = data.Connection;
        using var cmd = connection.CreateCommand(); cmd.Transaction = data.Transaction;
        cmd.CommandText = "SELECT count(*) FROM " + Quote(MemberTable(library, name, member));
        data.Prepare(cmd); return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Authorize(string library, string name, AuthorityBit permission)
    {
        library = ResolveLibrary(library);
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireObject(library.ToUpperInvariant(), name.ToUpperInvariant(), FileType, permission | AuthorityBit.ObjectOperate);
        authorization.RequireFileDataState(library.ToUpperInvariant(), name.ToUpperInvariant(), permission);
        AuthorizeLogicalSource(library, name, permission);
    }

    private void CreateFileCore(string library, string name, string attribute, FileDefinition definition, string? source, string? description, string member = "*FILE", CancellationToken cancellationToken = default, Ipc.Db.Sql.SqlNaming? sqlNaming = null, Ipc.Db.Sql.SqlExecutionOptions? sqlOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var libraryName = ResolveLibrary(library);
        var fileName = name.ToUpperInvariant();
        member = member.ToUpperInvariant(); if (member == "*FILE") member = fileName;
        if (member != "*NONE" && !ObjectName.IsValid(member)) throw LogicalError("Invalid physical member name.");

        if (_objects.Exists(libraryName, fileName, FileType))
        {
            throw new CpfException("CPF5805", $"File {fileName} already exists in library {libraryName}.");
        }

        using var establishment = new ConstraintAllocations();
        using var connection = _factory.Open();
        using var transaction = SqliteCancellation.BeginWrite(connection, cancellationToken);
        if (Ipc.Services.Events.OperationIdentity.Current?.Job is { } job) new JobDataAreaStore(_factory).RequireOwner(job);
        if (sqlOptions is not null)
        {
            BindSqlForeignKeys(connection, transaction, new(libraryName, fileName), definition, sqlOptions);
            SqlConstraintCatalog.ValidateLimits(connection, transaction, (new(libraryName, fileName), definition));
            EnsureForeignParents(connection, transaction, new(libraryName, fileName), definition, cancellationToken);
        }
        if (member != "*NONE") RequireEstablishmentAvailable(connection, transaction, new(libraryName, fileName), definition, cancellationToken);
        CreateFileImage(connection, transaction, libraryName, fileName, attribute, definition, source, description, member, sqlNaming);
        if (EstablishDefinedForeignKeys(connection, transaction, new(libraryName, fileName), establishment, cancellationToken))
        {
            var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
            authorization.RequireBoundObject(connection, transaction, "QSYS", libraryName, AuthorityBit.ObjectOperate | AuthorityBit.Add,
                objectType: ObjectType.Library, checkLibrary: false);
            var owner = ObjectSigningService.Read(connection, transaction, libraryName, fileName, FileType).Owner;
            if (sqlNaming == SqlNaming.Sql && Ipc.Services.Events.OperationIdentity.Current is { } caller && owner != caller.Principal)
                authorization.RequireBoundObject(connection, transaction, "QSYS", owner, AuthorityBit.Add, objectType: ObjectType.UserProfile, checkLibrary: false);
        }
        if (definition.SqlTable?.ForeignKeys is { Count: > 0 } && sqlOptions is not null)
        {
            if (ReadDefinition(connection, transaction, libraryName, fileName)?.ToJson() != definition.ToJson()) throw new CpfException("SQL0104", "Referential publication changed the intended table definition.");
            BindSqlForeignKeys(connection, transaction, new(libraryName, fileName), definition, sqlOptions);
            CheckSqlForeignRows(connection, transaction);
        }
        if (definition.SqlTable is not null) SqlConstraintCatalog.ValidateLimits(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    private void CreateFileImage(SqliteConnection connection, SqliteTransaction transaction, string libraryName, string fileName,
        string attribute, FileDefinition definition, string? source, string? description, string member,
        Ipc.Db.Sql.SqlNaming? sqlNaming = null, AuthorityBit? publicAuthority = null, string? owner = null)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction; exists.CommandText = "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*FILE'";
            exists.Parameters.AddWithValue("$lib", libraryName); exists.Parameters.AddWithValue("$name", fileName);
            if (Convert.ToInt64(exists.ExecuteScalar()) != 0) throw new CpfException("CPF5805", "File already exists.");
        }
        if (definition.SqlTable is not null) SqlConstraintCatalog.ValidateLimits(connection, transaction, (new(libraryName, fileName), definition));
        ValidateSqlConstraintNames(connection, transaction, libraryName, definition);
        var descriptor = new ObjectDescriptor
        {
            Key = new QualifiedName(libraryName, fileName),
            Owner = owner ?? _owner,
            ObjectType = FileType, Attribute = attribute, Source = source, Ccsid = definition.Ccsid,
            Description = description, Format = definition.PrimaryFormat.Name,
            PublicAuthority = publicAuthority ?? Authorities.UseBits,
        };
        if (sqlNaming is { } naming) CreateSqlTableDescriptor(connection, transaction, descriptor, naming);
        else _objects.Create(descriptor, connection, transaction);
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
            cmd.CommandText = CreateMemberTableSql(definition.PrimaryFormat, MemberTable(libraryName, fileName, member), definition.SqlTable)
                .Replace("IF NOT EXISTS ", "", StringComparison.Ordinal);
            cmd.ExecuteNonQuery();
            CreatePhysicalAccessPath(connection, transaction, libraryName, fileName, member, definition);
            CreateSqlConstraints(connection, transaction, MemberTable(libraryName, fileName, member), definition);
            JournalHooks.InstallMember(connection, transaction, libraryName, fileName, member);
        }
    }

    private string ResolveLibrary(string library) => new JobTemporaryLibrary(_factory).ResolveLibrary(library.ToUpperInvariant());

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

    private static string CreateMemberTableSql(RecordFormat format, string tableName, SqlTableDefinition? sqlTable = null, bool legacyCollations = false)
    {
        var columns = new List<string>();
        foreach (var field in format.Fields)
        {
            columns.Add(Quote(field.Name) + " " + SqliteType(field) + (sqlTable is not null && !legacyCollations ? " COLLATE " + Ipc.Db.Sql.SqlForeignSchema.Collation(field) : "") + (sqlTable is not null && !field.NullCapable ? " NOT NULL" : " NULL") +
                (sqlTable is null ? "" : SqlColumnDefault(field)));
        }

        if (sqlTable is not null)
            foreach (var primary in sqlTable.Constraints.Where(constraint => constraint.Primary))
                columns.Add("CHECK (" + string.Join(" AND ", primary.Columns.Select(name => Quote(name) + " IS NOT NULL")) + ")");

        if (sqlTable?.Checks is { } checks)
            foreach (var check in checks.Where(check => !check.Disabled)) columns.Add(Ipc.Db.Sql.SqlCheckRuntime.Sql(format, check));

        if (sqlTable?.ForeignKeys is { } foreign)
            foreach (var key in foreign.Where(Ipc.Db.Sql.SqlForeignSchema.Enforced)) columns.Add(Ipc.Db.Sql.SqlForeignSchema.Clause(format, key));

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



    private static string ToString(object value) => Ipc.Db.Sql.SqlExpressionRuntime.ValueText(value);

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
