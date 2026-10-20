using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed record BoundSqlIndex(QualifiedName Target, FileDefinition Definition, FileDefinition Physical, IReadOnlyList<string> Members);
    private BoundSqlIndex BindSqlIndex(SqliteConnection connection, SqliteTransaction transaction, SqlCreateIndex index, SqlExecutionOptions options)
    {
        var source = ResolveSqlObject(index.Source, options, false);
        var target = index.Name.Schema is not null || options.Naming == SqlNaming.Sql ? ResolveSqlObject(index.Name, options, true) : source with { Name = index.Name.Name };
        var temporary = new JobTemporaryLibrary(_factory);
        if (temporary.IsTemporary(source.Library) && source.Library != target.Library)
            throw new CpfException("SQL0104", "A permanent index cannot depend on a private QTEMP file.");
        var authorization = new ServiceAuthorization(_factory);
        authorization.RequireObject(source.Library, source.Name, FileType, AuthorityBit.ObjectAlter);
        RequireUnenlisted(source.Library, source.Name, "CREATE INDEX");
        if (new CommandDefinitionStore(_factory).Load("QSYS", "CRTLF").Builtin != "CRTLF") throw new CpfException("CPF9802", "CRTLF adapter changed.");
        using var dictionary = connection.CreateCommand(); dictionary.Transaction = transaction;
        dictionary.CommandText = "SELECT name FROM sys_objects WHERE lib=$lib AND type='*DTADCT'"; dictionary.Parameters.AddWithValue("$lib", target.Library);
        var dictionaries = new List<string>(); using (var reader = dictionary.ExecuteReader()) while (reader.Read()) dictionaries.Add(reader.GetString(0));
        foreach (var name in dictionaries) authorization.RequireObject(target.Library, name, ObjectType.DataDictionary, Authorities.ChangeBits);
        var physical = ReadDefinition(connection, transaction, source.Library, source.Name) ?? throw new CpfException("SQL0204", "Index base table not found.");
        if (physical.Attribute != FileAttribute.Physical || physical.Logical is not null || physical.Formats.Count != 1)
            throw new CpfException("SQL0104", "An index requires a single-format base table.");
        if (ReadDefinition(connection, transaction, target.Library, target.Name) is not null ||
            _objects.GetForAuthorization(target.Library, target.Name, FileType) is not null) throw new CpfException("SQL0601", "Index object name already exists.");
        var fields = physical.PrimaryFormat.Fields;
        foreach (var key in index.Keys) if (fields.All(field => field.Name != key.Column)) throw new CpfException("SQL0206", "Unknown index key " + key.Column + ".");
        if (index.Keys.Sum(key => fields.Single(field => field.Name == key.Column).StorageLength + (fields.Single(field => field.Name == key.Column).NullCapable ? 1 : 0)) > 32766)
            throw new CpfException("SQL0904", "Index key exceeds the supported byte length.");
        var selected = index.Columns ?? fields.Select(field => field.Name).ToArray();
        if (selected.Count > 1024 || selected.Any(name => fields.All(field => field.Name != name))) throw new CpfException("SQL0206", "Invalid index record columns.");
        var format = new RecordFormat { Name = index.Format ?? target.Name };
        foreach (var name in selected)
        {
            var field = JsonSerializer.Deserialize<FieldSpec>(JsonSerializer.Serialize(physical.PrimaryFormat.Find(name)!))!;
            field.Sequence = 0; field.Descending = false; format.Fields.Add(field);
        }
        for (var ordinal = 0; ordinal < index.Keys.Count; ordinal++)
        { var key = index.Keys[ordinal]; var field = format.Find(key.Column)!; field.Sequence = ordinal + 1; field.Descending = key.Descending; }
        format.AssignPositions();
        using var readMembers = connection.CreateCommand(); readMembers.Transaction = transaction;
        readMembers.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' ORDER BY created,mbr";
        readMembers.Parameters.AddWithValue("$lib", source.Library); readMembers.Parameters.AddWithValue("$name", source.Name);
        var members = new List<string>(); using (var reader = readMembers.ExecuteReader()) while (reader.Read()) members.Add(reader.GetString(0));
        if (members.Count > 256 || index.Unique && members.Count > 1) throw new CpfException("SQL0104", "Index member scope exceeds the implemented logical access paths.");
        var definition = new FileDefinition { Name = target.Name, Attribute = FileAttribute.Logical, SqlIndex = true, Ccsid = physical.Ccsid, Formats = [format],
            Logical = new() { SourceLibrary = source.Library, SourceFile = source.Name, SourceFormat = physical.PrimaryFormat.Name, Unique = index.Unique,
                ExcludeNullKeys = index.ExcludeNullKeys, MaximumMembers = 1, Members = new Dictionary<string, IReadOnlyList<string>> { [target.Name] = members } } };
        // Share SQL table ownership/public-authority rules; no descriptor is inserted during validation.
        CreateSqlTableDescriptor(connection, transaction, new() { Key = new(target.Library, target.Name), ObjectType = FileType, Attribute = "*LF", Owner = _owner }, options.Naming, validateOnly: true);
        return new(new(target.Library, target.Name), definition, physical, members);
    }
    private void CreateSqlIndex(SqlCreateIndex index, string sql, SqlExecutionOptions options, CancellationToken token)
    {
        var source = ResolveSqlObject(index.Source, options, false); var locks = new JobLockStore(_factory);
        using var gate = locks.EnterRecordWrite(token);
        new ServiceAuthorization(_factory).RequireObject(source.Library, source.Name, FileType, AuthorityBit.ObjectAlter);
        locks.RequireExclusiveRead(source.Library, source.Name, FileType);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var bound = BindSqlIndex(connection, transaction, index, options); var definition = bound.Definition;
        var target = bound.Target; var logical = definition.Logical!;
        var descriptor = new ObjectDescriptor { Key = target, ObjectType = FileType, Attribute = "*LF", Format = definition.PrimaryFormat.Name,
            Owner = _owner, Ccsid = definition.Ccsid, Source = sql };
        CreateSqlTableDescriptor(connection, transaction, descriptor, options.Naming);
        SqliteCancellation.Run(connection, token, () => {
            definition.Logical = logical with { UniquePaths = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat, bound.Physical.PrimaryFormat, bound.Members) };
            return 0;
        });
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_file_defs(lib,name,type,def) VALUES($lib,$name,'*FILE',$def);
            INSERT INTO sys_file_members(lib,name,type,mbr,created) VALUES($lib,$name,'*FILE',$name,$now);
            INSERT INTO sys_object_dependencies VALUES($lib,$name,'*FILE',$sourceLib,$sourceName,'*FILE');
            """;
        command.Parameters.AddWithValue("$lib", target.Library); command.Parameters.AddWithValue("$name", target.Name.Value);
        command.Parameters.AddWithValue("$def", definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sourceLib", logical.SourceLibrary); command.Parameters.AddWithValue("$sourceName", logical.SourceFile); command.ExecuteNonQuery();
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }
    private QualifiedName RequireSqlIndex(SqliteConnection connection, SqliteTransaction transaction, SqlObjectName name, SqlExecutionOptions options)
    {
        var target = ResolveSqlObject(name, options, false); var definition = ReadDefinition(connection, transaction, target.Library, target.Name)
            ?? throw new CpfException("SQL0204", "Index not found.");
        if (!definition.SqlIndex || definition.Logical is null) throw new CpfException("SQL0104", "DROP INDEX requires an SQL-created index.");
        new ServiceAuthorization(_factory).RequireObject(target.Library, target.Name, FileType, AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        RequireUnenlisted(definition.Logical.SourceLibrary, definition.Logical.SourceFile, "DROP INDEX");
        return new(target.Library, target.Name);
    }
    private void DropSqlIndex(SqlObjectName name, SqlExecutionOptions options, CancellationToken token)
    {
        using var gate = new JobLockStore(_factory).EnterRecordWrite(token);
        using var connection = _factory.Open(); using var transaction = SqliteCancellation.BeginWrite(connection, token);
        ValidateIndexPublisher(connection, transaction, token);
        var target = RequireSqlIndex(connection, transaction, name, options);
        new JobLockStore(_factory).RequireNoPersistentAllocation(new(target.Library, target.Name.Value, FileType));
        SqliteCancellation.Run(connection, token, () => { ObjectCatalogOperations.DeleteCore(target, FileType, connection, transaction); return 0; });
        ValidateIndexPublisher(connection, transaction, token); transaction.Commit();
    }
    private void ValidateIndexPublisher(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (OperationIdentity.Current is not { } identity) return;
        new ServiceAuthorization(_factory).RequireCaller(connection, transaction, identity.Principal);
        if (identity.Job is not { } job) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT cancel_requested FROM sys_jobs WHERE number=$number AND name=$name AND user=$user AND coalesce(user_profile,user)=$owner AND execution_state='Running'";
        JobRecordTransactions.BindJob(command, job); command.Parameters.AddWithValue("$owner", identity.Principal);
        var state = command.ExecuteScalar(); if (state is null) throw new CpfException("CPF1241", "SQL index job is no longer active.");
        if (Convert.ToInt32(state) != 0) throw new OperationCanceledException("SQL index job was canceled.");
    }
}
