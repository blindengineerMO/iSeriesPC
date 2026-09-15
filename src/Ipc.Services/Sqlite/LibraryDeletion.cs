using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Security;
using Ipc.Services.Work;

namespace Ipc.Services.Sqlite;

public sealed partial class ObjectCatalogOperations
{
    /// <summary>Delete a library and its contents in one catalog/payload/outbox transaction.</summary>
    public void DeleteLibrary(string library)
    {
        var key = new QualifiedName("QSYS", library);
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject("QSYS", library, ObjectType.Library, Authorities.UseBits | AuthorityBit.ObjectExist, checkLibrary: false);
        var locks = new JobLockStore(factory);
        locks.RequireNoPersistentAllocation(new("QSYS", library, ObjectType.Library));
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var root = new Operation(connection, transaction, key, key, ObjectType.Library);
        EnsureLibraryAvailable(root, library);
        if (root.Number("SELECT count(*) FROM sys_objects WHERE lib='QSYS' AND name=$name AND type='*LIB'") != 1)
            throw new CpfException("CPF2110", "Library not found.");
        var contents = new List<(QualifiedName Key, string Type)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT name,type FROM sys_objects WHERE lib=$library ORDER BY name,type LIMIT 4001";
            query.Parameters.AddWithValue("$library", library);
            using var reader = query.ExecuteReader();
            while (reader.Read()) contents.Add((new(library, reader.GetString(0)), reader.GetString(1)));
        }
        if (contents.Count > 4000) throw new CpfException("IPC0201", "DLTLIB supports at most 4000 objects per operation; remove objects in smaller batches first.");
        // These checks precede the first write, including authorization reads through other
        // connections. BEGIN IMMEDIATE holds the catalog snapshot throughout the deletion.
        foreach (var item in contents)
        {
            authorization.RequireObject(library, item.Key.Name.Value, item.Type, AuthorityBit.ObjectExist);
            locks.RequireNoPersistentAllocation(new(library, item.Key.Name.Value, item.Type));
        }
        // Remove menus' internal links first so mutually linked menus can be deleted together.
        using (var menus = connection.CreateCommand())
        {
            menus.Transaction = transaction;
            menus.CommandText = "DELETE FROM sys_menu_options WHERE library=$library";
            menus.Parameters.AddWithValue("$library", library); menus.ExecuteNonQuery();
        }
        // Each successful deletion releases its registered/named dependencies. A dependency
        // outside this library or an active object leaves no candidate and rolls back all work.
        while (contents.Count != 0)
        {
            var removed = false; CpfException? blocked = null;
            for (var index = contents.Count - 1; index >= 0; index--)
            {
                var item = contents[index];
                try { DeleteCore(item.Key, item.Type, connection, transaction); contents.RemoveAt(index); removed = true; }
                catch (CpfException error) when (error.MessageId == "IPC0201") { blocked = error; }
            }
            if (!removed) throw blocked ?? new CpfException("IPC0201", "Library contains objects that cannot be deleted.");
        }
        DeleteCore(key, ObjectType.Library, connection, transaction);
        transaction.Commit();
    }
}
