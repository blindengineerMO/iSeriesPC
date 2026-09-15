using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public int ChangePhysicalMemberLimit(string library, string name, int? maximumMembers)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant();
        if (maximumMembers is < 1 or > 32767) throw LogicalError("MAXMBRS requires 1–32767.");
        Authorize(library, name, AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var definition = GetDefinition(library, name) ?? throw new CpfException("CPF9801", "Physical file not found.");
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null) throw LogicalError("CHGPF requires a physical data file.");
        if (maximumMembers is null) { transaction.Commit(); return definition.MaximumMembers; }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt64(command.ExecuteScalar()) > maximumMembers) throw LogicalError("MAXMBRS cannot be less than the existing member count.");
        definition.MaximumMembers = maximumMembers.Value;
        command.CommandText = """
            UPDATE sys_file_defs SET def=$definition WHERE lib=$lib AND name=$name AND type='*FILE';
            UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE';
            """;
        command.Parameters.AddWithValue("$definition", definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit(); return maximumMembers.Value;
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
