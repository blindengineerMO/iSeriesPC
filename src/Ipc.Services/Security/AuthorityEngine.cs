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
        new ServiceAuthorization(_factory).RequireObject(library, name, type, AuthorityBit.ObjectManagement);
        var descriptor = _objects.GetRequired(library, name, type);
        descriptor.PublicAuthority = bits;
        _objects.Update(descriptor);
    }

    public void Grant(string library, string name, string type, string userOrGroup, AuthorityBit bits)
    {
        new ServiceAuthorization(_factory).RequireObject(library, name, type, AuthorityBit.ObjectManagement);
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
        new ServiceAuthorization(_factory).RequireObject(library, name, type, AuthorityBit.ObjectManagement);
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
        new ServiceAuthorization(_factory).RequireObject(library, name, type, AuthorityBit.ObjectManagement);
        if (GetDescriptor("QSYS", authl.ToUpperInvariant(), ObjectType.AuthorizationList) is null)
            new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureAuthorizationList(connection, transaction, authl);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
        transaction.Commit();
    }

    public void AddAuthLMember(string authl, string userOrGroup, AuthorityBit bits)
    {
        var authorization = new ServiceAuthorization(_factory);
        if (GetDescriptor("QSYS", authl.ToUpperInvariant(), ObjectType.AuthorizationList) is null)
            authorization.RequireSpecial(SpecialAuthority.SecurityAdministrator);
        else authorization.RequireObject("QSYS", authl.ToUpperInvariant(), ObjectType.AuthorizationList, AuthorityBit.ObjectManagement);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureAuthorizationList(connection, transaction, authl);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sys_authl_members (authl, holder, bits)
            VALUES ($authl, $holder, $bits)
            ON CONFLICT(authl, holder) DO UPDATE SET bits = excluded.bits;
            """;
        cmd.Parameters.AddWithValue("$authl", authl.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$holder", userOrGroup.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$bits", (int)bits);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureAuthorizationList(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, string name)
    {
        name = name.ToUpperInvariant();
        if (!ObjectName.IsValid(name)) throw new ArgumentException("A valid authorization list name is required.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_objects(lib,name,type,owner,created,changed,public_authority)
            VALUES('QSYS',$name,'*AUTL','QSECOFR',$now,$now,0)
            ON CONFLICT(lib,name,type) DO NOTHING
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveAuthLMember(string authl, string userOrGroup)
    {
        var authorization = new ServiceAuthorization(_factory);
        if (GetDescriptor("QSYS", authl.ToUpperInvariant(), ObjectType.AuthorizationList) is null)
            authorization.RequireSpecial(SpecialAuthority.SecurityAdministrator);
        else authorization.RequireObject("QSYS", authl.ToUpperInvariant(), ObjectType.AuthorizationList, AuthorityBit.ObjectManagement);
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
        if (SecurityLevel <= 20 || (user.SpecialAuthorities & SpecialAuthority.AllObject) != 0)
            return Authorities.AllBits;
        using var connection = _factory.Open();
        var personal = ProfileAuthority(connection, user.Name, descriptor);
        if (personal is { } individual) return individual;
        AuthorityBit? groupBits = null;
        foreach (var group in groups.Distinct(StringComparer.Ordinal))
        {
            using var special = connection.CreateCommand();
            special.CommandText = "SELECT special_auth FROM sys_profiles WHERE name=$name";
            special.Parameters.AddWithValue("$name", group);
            var groupSpecial = (SpecialAuthority)Convert.ToInt32(special.ExecuteScalar());
            if ((groupSpecial & SpecialAuthority.AllObject) != 0) return Authorities.AllBits;
            var authority = ProfileAuthority(connection, group, descriptor);
            if (authority is { } found) groupBits = (groupBits ?? AuthorityBit.None) | found;
        }
        if (groupBits is { } combined) return combined;
        if (descriptor.UseAuthorizationListPublicAuthority)
        {
            var result = AuthorityBit.None;
            foreach (var attachment in Attachments(connection, descriptor))
            {
                var list = GetDescriptor("QSYS", attachment.Name, ObjectType.AuthorizationList);
                if (list is not null) result |= list.PublicAuthority & attachment.Mask;
            }
            return result;
        }
        return descriptor.PublicAuthority;
    }

    internal AuthorityBit AdoptedAuthority(string owner, ObjectDescriptor descriptor)
    {
        using var connection = _factory.Open();
        using var special = connection.CreateCommand();
        special.CommandText = "SELECT special_auth FROM sys_profiles WHERE name=$name";
        special.Parameters.AddWithValue("$name", owner);
        if (special.ExecuteScalar() is not long bits) return AuthorityBit.None;
        if (((SpecialAuthority)bits & SpecialAuthority.AllObject) != 0) return Authorities.AllBits;
        // Adopted authority includes the owner's individual authority, never its groups/public fallback.
        return ProfileAuthority(connection, owner, descriptor) ?? AuthorityBit.None;
    }

    private static AuthorityBit? ProfileAuthority(Microsoft.Data.Sqlite.SqliteConnection connection,
        string profile, ObjectDescriptor descriptor)
    {
        var direct = PrivateAuthority(connection, descriptor, profile);
        if (direct is not null) return direct;
        if (descriptor.Owner == profile) return Authorities.AllBits;
        AuthorityBit? result = null;
        foreach (var attachment in Attachments(connection, descriptor))
        {
            using var member = connection.CreateCommand();
            member.CommandText = "SELECT bits FROM sys_authl_members WHERE authl=$list AND holder=$profile";
            member.Parameters.AddWithValue("$list", attachment.Name);
            member.Parameters.AddWithValue("$profile", profile);
            if (member.ExecuteScalar() is long bits)
                result = (result ?? AuthorityBit.None) | ((AuthorityBit)bits & attachment.Mask);
            else
            {
                using var owner = connection.CreateCommand();
                owner.CommandText = "SELECT owner FROM sys_objects WHERE lib='QSYS' AND name=$list AND type='*AUTL'";
                owner.Parameters.AddWithValue("$list", attachment.Name);
                if (owner.ExecuteScalar() as string == profile) result = (result ?? AuthorityBit.None) | attachment.Mask;
            }
        }
        return result;
    }

    private static AuthorityBit? PrivateAuthority(Microsoft.Data.Sqlite.SqliteConnection connection,
        ObjectDescriptor descriptor, string holder)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bits FROM sys_authorities WHERE lib=$lib AND name=$name AND type=$type AND holder=$holder AND is_authl=0";
        command.Parameters.AddWithValue("$lib", descriptor.Library);
        command.Parameters.AddWithValue("$name", descriptor.Name);
        command.Parameters.AddWithValue("$type", descriptor.ObjectType);
        command.Parameters.AddWithValue("$holder", holder);
        return command.ExecuteScalar() is long bits ? (AuthorityBit)bits : null;
    }

    private static List<(string Name, AuthorityBit Mask)> Attachments(Microsoft.Data.Sqlite.SqliteConnection connection,
        ObjectDescriptor descriptor)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT holder,bits FROM sys_authorities WHERE lib=$lib AND name=$name AND type=$type AND is_authl=1 ORDER BY holder";
        command.Parameters.AddWithValue("$lib", descriptor.Library);
        command.Parameters.AddWithValue("$name", descriptor.Name);
        command.Parameters.AddWithValue("$type", descriptor.ObjectType);
        using var reader = command.ExecuteReader();
        var result = new List<(string, AuthorityBit)>();
        while (reader.Read()) result.Add((reader.GetString(0), (AuthorityBit)reader.GetInt32(1)));
        return result;
    }

    public bool CanExecute(
        UserProfile user, IReadOnlyList<string> groups,
        string library, string name, string type, AuthorityBit required)
    {
        var descriptor = GetDescriptor(library, name, type);
        if (descriptor is null)
        {
            return false;
        }

        var effective = ComputeEffective(user, groups, descriptor);
        return (effective & required) == required;
    }

    public AuthorityBit EffectiveFor(
        UserProfile user, IReadOnlyList<string> groups, string library, string name, string type)
    {
        var descriptor = GetDescriptor(library, name, type);
        if (descriptor is null)
        {
            return AuthorityBit.None;
        }

        return ComputeEffective(user, groups, descriptor);
    }

    private ObjectDescriptor? GetDescriptor(string library, string name, string type) =>
        _objects is SqliteObjectStore sqlite ? sqlite.GetForAuthorization(library, name, type) : _objects.Get(library, name, type);

}
