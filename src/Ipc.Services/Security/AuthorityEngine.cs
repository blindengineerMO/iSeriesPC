using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public sealed class AuthorityEngine
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IObjectStore _objects;

    public AuthorityEngine(SqliteConnectionFactory factory, IObjectStore objects)
    {
        _factory = factory;
        _objects = objects;
    }

    public int SecurityLevel
    {
        get
        {
            var store = new Sqlite.SystemValueStore(_factory);
            var registry = new SystemValueRegistry();
            store.LoadInto(registry);
            var value = registry.Get(SystemValueNames.SecurityLevel).Value;
            return int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var level)
                ? level
                : 40;
        }
    }

    public void SetPublicAuthority(string library, string name, string type, AuthorityBit bits)
    {
        var descriptor = _objects.GetRequired(library, name, type);
        descriptor.PublicAuthority = bits;
        _objects.Update(descriptor);
    }

    public void Grant(string library, string name, string type, string userOrGroup, AuthorityBit bits)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_authorities (lib, name, type, holder, is_authl, bits)
            VALUES ($lib, $name, $type, $holder, 0, $bits)
            ON CONFLICT(lib, name, type, holder, is_authl) DO UPDATE SET bits = excluded.bits;
            """;
        cmd.Parameters.AddWithValue("$lib", library);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$holder", userOrGroup.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$bits", (int)bits);
        cmd.ExecuteNonQuery();
    }

    public void Revoke(string library, string name, string type, string userOrGroup)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM sys_authorities WHERE lib = $lib AND name = $name AND type = $type " +
            "AND holder = $holder AND is_authl = 0";
        cmd.Parameters.AddWithValue("$lib", library);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$holder", userOrGroup.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    public void GrantAuthL(string library, string name, string type, string authl, AuthorityBit bits)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_authorities (lib, name, type, holder, is_authl, bits)
            VALUES ($lib, $name, $type, $holder, 1, $bits)
            ON CONFLICT(lib, name, type, holder, is_authl) DO UPDATE SET bits = excluded.bits;
            """;
        cmd.Parameters.AddWithValue("$lib", library);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$holder", authl.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$bits", (int)bits);
        cmd.ExecuteNonQuery();
    }

    public void AddAuthLMember(string authl, string userOrGroup, AuthorityBit bits)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_authl_members (authl, holder, bits)
            VALUES ($authl, $holder, $bits)
            ON CONFLICT(authl, holder) DO UPDATE SET bits = excluded.bits;
            """;
        cmd.Parameters.AddWithValue("$authl", authl.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$holder", userOrGroup.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$bits", (int)bits);
        cmd.ExecuteNonQuery();
    }

    public void RemoveAuthLMember(string authl, string userOrGroup)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sys_authl_members WHERE authl = $authl AND holder = $holder";
        cmd.Parameters.AddWithValue("$authl", authl.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$holder", userOrGroup.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    public AuthorityBit ComputeEffective(
        UserProfile user, IReadOnlyList<string> groups, ObjectDescriptor descriptor)
    {
        if ((user.SpecialAuthorities & SpecialAuthority.AllObject) != 0)
        {
            return Authorities.AllBits;
        }

        var holders = new List<string>();
        holders.Add(user.Name);
        holders.AddRange(groups);

        var level = SecurityLevel;
        if (level <= 10)
        {
            return Authorities.AllBits;
        }

        var bits = descriptor.PublicAuthority;

        bits |= Aggregate(descriptor.Key.Library, descriptor.Name, descriptor.ObjectType,
            holders, includeAuthL: false);

        bits |= Aggregate(descriptor.Key.Library, descriptor.Name, descriptor.ObjectType,
            holders, includeAuthL: true);

        return bits;
    }

    public bool CanExecute(
        UserProfile user, IReadOnlyList<string> groups,
        string library, string name, string type, AuthorityBit required)
    {
        var descriptor = FindAny(library, name);
        if (descriptor is null)
        {
            return (user.SpecialAuthorities & SpecialAuthority.AllObject) != 0;
        }

        var effective = ComputeEffective(user, groups, descriptor);
        return (effective & required) == required;
    }

    public AuthorityBit EffectiveFor(
        UserProfile user, IReadOnlyList<string> groups, string library, string name, string type)
    {
        var descriptor = FindAny(library, name);
        if (descriptor is null)
        {
            return AuthorityBit.None;
        }

        return ComputeEffective(user, groups, descriptor);
    }

    private ObjectDescriptor? FindAny(string library, string name) =>
        _objects.Find(library, name, null, null).FirstOrDefault();

    private AuthorityBit Aggregate(string library, string name, string type,
        IReadOnlyList<string> holders, bool includeAuthL)
    {
        var result = AuthorityBit.None;
        using var connection = _factory.Open();

        foreach (var holder in holders)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT bits FROM sys_authorities WHERE lib = $lib AND name = $name AND type = $type " +
                    "AND holder = $holder AND is_authl = $isauthl";
                cmd.Parameters.AddWithValue("$lib", library);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$holder", holder);
                cmd.Parameters.AddWithValue("$isauthl", includeAuthL ? 1 : 0);
                if (cmd.ExecuteScalar() is long value)
                {
                    result |= (AuthorityBit)value;
                }
            }

            if (!includeAuthL)
            {
                continue;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT holder FROM sys_authorities WHERE lib = $lib AND name = $name AND type = $type " +
                    "AND is_authl = 1";
                cmd.Parameters.AddWithValue("$lib", library);
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$type", type);
                var authls = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    authls.Add(reader.GetString(0));
                }

                foreach (var authl in authls)
                {
                    result |= AuthLMemberBits(authl, holders);
                }
            }
        }

        return result;
    }

    private AuthorityBit AuthLMemberBits(string authl, IReadOnlyList<string> holders)
    {
        var result = AuthorityBit.None;
        using var connection = _factory.Open();
        foreach (var holder in holders)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT bits FROM sys_authl_members WHERE authl = $authl AND holder = $holder";
            cmd.Parameters.AddWithValue("$authl", authl);
            cmd.Parameters.AddWithValue("$holder", holder);
            if (cmd.ExecuteScalar() is long value)
            {
                result |= (AuthorityBit)value;
            }
        }

        return result;
    }
}