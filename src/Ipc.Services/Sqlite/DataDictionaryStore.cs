using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Security;

namespace Ipc.Services.Sqlite;

/// <summary>Dictionary creation and authority attachment are one catalog operation.</summary>
public sealed class DataDictionaryStore(SqliteConnectionFactory factory)
{
    public void Create(string name, string text = "", string authority = "*LIBCRTAUT")
    {
        if (!ObjectName.IsValid(name) || ObjectName.IsSystemLibrary(name)) throw new CpfException("CPF2D71", "Invalid data dictionary name.");
        if (text == "*BLANK") text = "";
        if (text.Length > 50 || text.Any(char.IsControl)) throw new CpfException("IPC0003", "Dictionary text must contain at most 50 printable characters.");
        var objects = new SqliteObjectStore(factory);
        var authorization = new ServiceAuthorization(factory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var library = objects.GetRequired("QSYS", name, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var isList = ObjectName.IsValid(authority);
        if (isList)
        {
            _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList);
            authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        }
        AuthorityBit bits;
        try { bits = isList ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw new CpfException("IPC0003", "Unsupported dictionary authority."); }
        if (objects.Exists(name, name, ObjectType.DataDictionary)) throw new CpfException("CPF2F04", "Data dictionary already exists.");
        var descriptor = new ObjectDescriptor
        {
            Key = new(name, name), ObjectType = ObjectType.DataDictionary,
            Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", Description = text,
            PublicAuthority = bits, UseAuthorizationListPublicAuthority = isList,
            Source = "{\"version\":1,\"definitions\":{}}",
        };
        objects.Create(descriptor, connection, transaction);
        if (isList)
        {
            using var attach = connection.CreateCommand(); attach.Transaction = transaction;
            attach.CommandText = "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($name,$name,'*DTADCT',$list,1,$bits)";
            attach.Parameters.AddWithValue("$name", name); attach.Parameters.AddWithValue("$list", authority);
            attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
