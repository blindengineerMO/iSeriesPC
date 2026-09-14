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
            using var connection = _factory.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys_objects";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void Create(ObjectDescriptor descriptor)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
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
        descriptor.Touch();
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sys_objects SET
                owner = $owner, created = $created, changed = $changed, description = $description,
                ccsid = $ccsid, attribute = $attribute, format = $format,
                public_authority = $pubauth, source = $source, attrs = $attrs
            WHERE lib = $lib AND name = $name AND type = $type
            """;
        BindDescriptor(cmd, descriptor);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string library, string name, string type)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sys_objects WHERE lib = $lib AND name = $name AND type = $type";
        cmd.Parameters.AddWithValue("$lib", library);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.ExecuteNonQuery();
    }

    public bool Exists(string library, string name, string type) =>
        Get(library, name, type) is not null;

    public ObjectDescriptor? Get(string library, string name, string type)
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

    public IReadOnlyList<ObjectDescriptor> Find(
        string library, string? namePattern, string? type, string? owner)
    {
        var sql = new System.Text.StringBuilder(
            "SELECT * FROM sys_objects WHERE lib = $lib");
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
                sql.Append(" AND name LIKE $name");
                parameters["$name"] = namePattern[..^1] + "%";
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
            list.Add(ReadDescriptor(reader));
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
            list.Add(reader.GetString(0));
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

    private static ObjectDescriptor ReadDescriptor(SqliteDataReader reader) =>
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