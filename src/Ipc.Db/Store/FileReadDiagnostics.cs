using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(string library, string name, string member)
        => ReadWithDiagnostics(() => ReadAllCore(library, name, member));
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyed(string library, string name, string member)
        => ReadWithDiagnostics(() => ReadKeyedCore(library, name, member));
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefix(string library, string name, string member, IReadOnlyDictionary<string, object?> prefix)
        => ReadWithDiagnostics(() => ReadKeyPrefixCore(library, name, member, prefix));

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadWithDiagnostics(Func<IReadOnlyList<IReadOnlyDictionary<string, object?>>> read)
    {
        try { return read(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 1) { throw LogicalError("Invalid stored file value or query definition."); }
    }
}
