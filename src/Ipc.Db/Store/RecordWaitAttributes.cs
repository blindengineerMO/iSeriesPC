using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public void ChangeLogicalRecordWait(string library, string name, int? seconds)
    {
        library = ResolveLibrary(library).ToUpperInvariant(); name = name.ToUpperInvariant();
        if (seconds is < 0 or > 32767) throw LogicalError("WAITRCD requires 0–32767 seconds.");
        AuthorizeFileManagement(library, name, AuthorityBit.None);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("CPF9801", "Logical file not found.");
        if (definition.Attribute != FileAttribute.Logical || definition.Logical is null) throw LogicalError("CHGLF requires a logical file.");
        if (seconds is null) { transaction.Commit(); return; }
        definition.RecordLockWaitSeconds = seconds.Value;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE sys_file_defs SET def=$definition WHERE lib=$lib AND name=$name AND type='*FILE';
            UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE';
            """;
        command.Parameters.AddWithValue("$definition", definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery(); transaction.Commit();
    }
}
