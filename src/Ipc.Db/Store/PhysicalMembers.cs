using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public int ChangePhysicalMemberLimit(string library, string name, int? maximumMembers, int? recordLockWaitSeconds = null)
    {
        library = ResolveLibrary(library);
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant();
        if (maximumMembers is < 1 or > 32767) throw LogicalError("MAXMBRS requires 1–32767.");
        if (recordLockWaitSeconds is < 0 or > 32767) throw LogicalError("WAITRCD requires 0–32767 seconds.");
        Authorize(library, name, AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Physical file not found.");
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null) throw LogicalError("CHGPF requires a physical data file.");
        if (maximumMembers is null && recordLockWaitSeconds is null) { transaction.Commit(); return definition.MaximumMembers; }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        if (maximumMembers is not null && Convert.ToInt64(command.ExecuteScalar()) > maximumMembers) throw LogicalError("MAXMBRS cannot be less than the existing member count.");
        if (maximumMembers is not null) definition.MaximumMembers = maximumMembers.Value;
        if (recordLockWaitSeconds is not null) definition.RecordLockWaitSeconds = recordLockWaitSeconds.Value;
        command.CommandText = """
            UPDATE sys_file_defs SET def=$definition WHERE lib=$lib AND name=$name AND type='*FILE';
            UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE';
            """;
        command.Parameters.AddWithValue("$definition", definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit(); return definition.MaximumMembers;
    }

    private static void CheckNewPhysicalMember(SqliteConnection connection, SqliteTransaction transaction,
        string library, string file, string member, FileDefinition definition)
    {
        if (!ObjectName.IsValid(member) || definition.MaximumMembers is < 1 or > 32767) throw LogicalError("Invalid member name or maximum member count.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*),coalesce(sum(mbr=$member),0) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", file); command.Parameters.AddWithValue("$member", member);
        using var reader = command.ExecuteReader(); reader.Read();
        if (reader.GetInt64(1) != 0) throw new CpfException("CPF5812", "Physical member already exists.");
        if (reader.GetInt64(0) >= definition.MaximumMembers) throw LogicalError("Physical file has reached its maximum member count.");
    }
}
