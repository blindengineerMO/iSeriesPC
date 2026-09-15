using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

public sealed class SqliteObjectStore : IObjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnectionFactory _factory;

    public SqliteObjectStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public long ObjectCount
    {
        get
        {
            if (Ipc.Services.Events.OperationIdentity.Current is not null)
                return ListLibraries().Sum(library => (long)Find(library, null, null, null).Count);
            using var connection = _factory.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys_objects";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void Create(ObjectDescriptor descriptor)
    {
        using var connection = _factory.Open();
        Create(descriptor, connection, null);
    }

    public void Create(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (descriptor.ObjectType == ObjectType.Command && transaction is null)
        {
            using var commandTransaction = connection.BeginTransaction(deferred: false);
            Create(descriptor, connection, commandTransaction); commandTransaction.Commit(); return;
        }
        descriptor.ValidateIdentity();
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireCreate(descriptor);
        var definition = descriptor.ObjectType == ObjectType.Command ? Ipc.Services.Commands.CommandDefinitionStore.ValidatePayload(descriptor) : null;
        var commands = new Ipc.Services.Commands.CommandDefinitionStore(_factory);
        if (definition is not null) commands.AuthorizeDependencies(definition, connection, transaction!);
        Insert(descriptor, connection, transaction);
        if (definition is not null) commands.BindDependencies(descriptor, definition, connection, transaction!);
    }

    // Caller must authorize the complete batch before its first mutation, in this transaction.
    internal void CreateAuthorized(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction transaction) => Insert(descriptor, connection, transaction);
    private static void Insert(ObjectDescriptor descriptor, SqliteConnection connection, SqliteTransaction? transaction)
    {
        descriptor.ValidateIdentity();
        if (descriptor.ObjectType == ObjectType.UserProfile)
            throw new CpfException("IPC0003", "Create profiles through the profile service so credentials and metadata remain consistent.");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_objects
                (lib, name, type, owner, created, changed, description, ccsid, attribute, format,
                 public_authority, source, attrs)
            VALUES ($lib, $name, $type, $owner, $created, $changed, $description, $ccsid, $attribute,
                    $format, $pubauth, $source, $attrs)
            """;
        BindDescriptor(cmd, descriptor);
        cmd.ExecuteNonQuery();
    }

    public void Update(ObjectDescriptor descriptor)
    {
        descriptor.ValidateIdentity();
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectManagement);
        if (descriptor.ObjectType == ObjectType.Program && descriptor.Attribute == Ipc.Services.Work.ExternalProgramService.Attribute)
            authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.Service, allowAdopted: false);
        authorization.RequireAdoption(descriptor);
        var previous = GetForAuthorization(descriptor.Library, descriptor.Name, descriptor.ObjectType);
        if (previous is not null && previous.Owner != descriptor.Owner)
            authorization.RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectExist);
        descriptor.Touch();
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var definition = descriptor.ObjectType == ObjectType.Command ? Ipc.Services.Commands.CommandDefinitionStore.ValidatePayload(descriptor) : null;
        var commands = new Ipc.Services.Commands.CommandDefinitionStore(_factory);
        if (definition is not null) commands.AuthorizeDependencies(definition, connection, transaction);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE sys_objects SET
                owner = $owner, created = $created, changed = $changed, description = $description,
                ccsid = $ccsid, attribute = $attribute, format = $format,
                public_authority = $pubauth, source = $source, attrs = $attrs
            WHERE lib = $lib AND name = $name AND type = $type
            """;
        BindDescriptor(cmd, descriptor);
        if (cmd.ExecuteNonQuery() != 1)
            throw new CpfException("CPF9801", $"Object {descriptor.ObjectType} {descriptor.Key} not found.");
        if (descriptor.ObjectType == ObjectType.UserProfile)
        {
            using var profile = connection.CreateCommand();
            profile.Transaction = transaction;
            profile.CommandText = "UPDATE sys_profiles SET owner=$owner,description=$description,ccsid=$ccsid WHERE name=$name AND user_class=$attribute";
            BindDescriptor(profile, descriptor);
            if (profile.ExecuteNonQuery() != 1)
                throw new CpfException("IPC0003", "Profile class changes require the profile service.");
        }
        if (descriptor.ObjectType is ObjectType.SubsystemDescription or ObjectType.JobQueue)
        {
            using var domain = connection.CreateCommand();
            domain.Transaction = transaction;
            var table = descriptor.ObjectType == ObjectType.SubsystemDescription ? "sys_subsystems" : "sys_jobqs";
            domain.CommandText = $"UPDATE {table} SET description=$description WHERE library=$lib AND name=$name";
            BindDescriptor(domain, descriptor);
            domain.ExecuteNonQuery();
        }
        if (definition is not null) commands.BindDependencies(descriptor, definition, connection, transaction);
        transaction.Commit();
    }

    public void Delete(string library, string name, string type)
    {
        new ObjectCatalogOperations(_factory).Delete(new QualifiedName(library, name), type);
    }

    public bool Exists(string library, string name, string type) =>
        Get(library, name, type) is not null;

    public ObjectDescriptor? Get(string library, string name, string type)
    {
        var descriptor = GetForAuthorization(library, name, type);
        if (descriptor is not null) new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(library, name, type, Authorities.UseBits);
        return descriptor;
    }

    internal ObjectDescriptor? GetForAuthorization(string library, string name, string type)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM sys_objects WHERE lib = $lib AND name = $name AND type = $type";
        cmd.Parameters.AddWithValue("$lib", library);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDescriptor(reader) : null;
    }

    public ObjectDescriptor GetRequired(string library, string name, string type) =>
        Get(library, name, type)
        ?? throw new CpfException("CPF9801", $"Object {type} {library}/{name} not found.");

    public IReadOnlyList<ObjectDescriptor> Find(string library, string? namePattern, string? type, string? owner) =>
        FindCore(library, namePattern, type, owner, int.MaxValue, true);

    public IReadOnlyList<ObjectDescriptor> FindSummaries(string library, string? namePattern, string? type, int maximum = 4001)
    {
        if (maximum is < 1 or > 4001) throw new ArgumentOutOfRangeException(nameof(maximum));
        return FindCore(library, namePattern, type, null, maximum, false);
    }

    private IReadOnlyList<ObjectDescriptor> FindCore(string library, string? namePattern, string? type, string? owner, int maximum, bool payload)
    {
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject("QSYS", library, ObjectType.Library, AuthorityBit.ObjectOperate, checkLibrary: false);
        var sql = new System.Text.StringBuilder("SELECT " + (payload ? "*" : "lib,name,type,owner,created,changed,description,ccsid,attribute,NULL,public_authority,NULL,NULL") + " FROM sys_objects WHERE lib = $lib");
        var parameters = new Dictionary<string, object?> { ["$lib"] = library };

        if (type is not null)
        {
            sql.Append(" AND type = $type");
            parameters["$type"] = type;
        }

        if (owner is not null)
        {
            sql.Append(" AND owner = $owner");
            parameters["$owner"] = owner;
        }

        if (namePattern is not null)
        {
            if (namePattern.EndsWith("*", StringComparison.Ordinal))
            {
                sql.Append(" AND name LIKE $name ESCAPE '\\'");
                parameters["$name"] = namePattern[..^1].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            }
            else
            {
                sql.Append(" AND name = $name");
                parameters["$name"] = namePattern;
            }
        }

        sql.Append(" ORDER BY name COLLATE NOCASE");

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
        }

        using var reader = cmd.ExecuteReader();
        var list = new List<ObjectDescriptor>();
        while (reader.Read())
        {
            var descriptor = ReadDescriptor(reader);
            try
            {
                new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(descriptor.Library, descriptor.Name, descriptor.ObjectType, Authorities.UseBits, checkLibrary: false);
                list.Add(descriptor);
                if (list.Count == maximum) break;
            }
            catch (CpfException ex) when (ex.MessageId == "CPF9802") { }
        }

        return list;
    }

    public IReadOnlyList<string> ListLibraries()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT name FROM sys_objects WHERE type = '*LIB' ORDER BY name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            var library = reader.GetString(0);
            try
            {
                new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject("QSYS", library, ObjectType.Library, AuthorityBit.ObjectOperate, checkLibrary: false);
                list.Add(library);
            }
            catch (CpfException ex) when (ex.MessageId == "CPF9802") { }
        }

        return list;
    }

    private static void BindDescriptor(SqliteCommand cmd, ObjectDescriptor d)
    {
        cmd.Parameters.AddWithValue("$lib", d.Library);
        cmd.Parameters.AddWithValue("$name", d.Name);
        cmd.Parameters.AddWithValue("$type", d.ObjectType);
        cmd.Parameters.AddWithValue("$owner", d.Owner);
        cmd.Parameters.AddWithValue("$created", d.Created.ToString("O"));
        cmd.Parameters.AddWithValue("$changed", d.Changed.ToString("O"));
        cmd.Parameters.AddWithValue("$description", (object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ccsid", d.Ccsid);
        cmd.Parameters.AddWithValue("$attribute", (object?)d.Attribute ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$format", (object?)d.Format ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pubauth", (int)d.PublicAuthority);
        cmd.Parameters.AddWithValue("$source", (object?)d.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attrs",
            d.ExtendedAttributes is null ? DBNull.Value : JsonSerializer.Serialize(d.ExtendedAttributes, JsonOptions));
    }

    internal static ObjectDescriptor ReadDescriptor(SqliteDataReader reader) =>
        new()
        {
            Key = new QualifiedName(reader.GetString(0), reader.GetString(1)),
            ObjectType = reader.GetString(2),
            Owner = reader.GetString(3),
            Created = DateTimeOffset.Parse(reader.GetString(4)),
            Changed = DateTimeOffset.Parse(reader.GetString(5)),
            Description = reader.IsDBNull(6) ? null : reader.GetString(6),
            Ccsid = reader.GetInt32(7),
            Attribute = reader.IsDBNull(8) ? null : reader.GetString(8),
            Format = reader.IsDBNull(9) ? null : reader.GetString(9),
            PublicAuthority = (AuthorityBit)reader.GetInt32(10),
            Source = reader.IsDBNull(11) ? null : reader.GetString(11),
            ExtendedAttributes = reader.IsDBNull(12)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(12), JsonOptions),
        };
}
