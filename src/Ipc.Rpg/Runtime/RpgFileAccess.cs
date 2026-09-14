using Ipc.Db.Definitions;
using Ipc.Db.Store;

namespace Ipc.Rpg.Runtime;

public sealed class RpgSqliteFileAccess : IRpgFileAccess
{
    private readonly SqliteFileStore _store;

    public RpgSqliteFileAccess(SqliteFileStore store) => _store = store;

    public FileDefinition? GetDefinition(string library, string name) =>
        _store.GetDefinition(library, name);

    public bool MemberExists(string library, string name, string member) =>
        _store.MemberExists(library, name, member);

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(string library, string name, string member) =>
        _store.ReadAll(library, name, member);

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyed(string library, string name, string member) =>
        _store.ReadKeyed(library, name, member);

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadKeyPrefix(
        string library, string name, string member, IReadOnlyDictionary<string, object?> prefix) =>
        _store.ReadKeyPrefix(library, name, member, prefix);

    public void Insert(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values) =>
        _store.Insert(library, name, member, format, values);

    public void Update(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> values) =>
        _store.Update(library, name, member, format, values);

    public void Delete(string library, string name, string member, string format, IReadOnlyDictionary<string, object?> keyValues) =>
        _store.Delete(library, name, member, format, keyValues);

    public long RowCount(string library, string name, string member) =>
        _store.RowCount(library, name, member);
}