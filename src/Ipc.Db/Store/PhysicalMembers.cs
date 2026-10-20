using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public int ChangePhysicalMemberLimit(string library, string name, int? maximumMembers, int? recordLockWaitSeconds = null, bool? reuseDeletedRecords = null, FileMemberSizeChange? memberSize = null)
    {
        if (memberSize is { InitialRecords: null, IncrementRecords: null, MaximumIncrements: null, Unlimited: null }) memberSize = null;
        library = ResolveLibrary(library);
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant();
        if (maximumMembers is < 1 or > 32767) throw LogicalError("MAXMBRS requires 1–32767.");
        if (recordLockWaitSeconds is < 0 or > 32767) throw LogicalError("WAITRCD requires 0–32767 seconds.");
        AuthorizeFileManagement(library, name, AuthorityBit.None);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var definition = ReadDefinition(connection, transaction, library, name) ?? throw new CpfException("CPF9801", "Physical file not found.");
        if (definition.Attribute != FileAttribute.Physical || definition.Logical is not null) throw LogicalError("CHGPF requires a physical data file.");
        if (maximumMembers is > 1 && ((definition.SqlTable?.ForeignKeys?.Count ?? 0) != 0 || Ipc.Services.Sqlite.SqlForeignCatalog.IsParent(connection,transaction,new(library,name))))
            throw LogicalError("Referential constraints require MAXMBRS(1).");
        if (maximumMembers is null && recordLockWaitSeconds is null && reuseDeletedRecords is null && memberSize is null) { transaction.Commit(); return definition.MaximumMembers; }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        if (maximumMembers is not null && Convert.ToInt64(command.ExecuteScalar()) > maximumMembers) throw LogicalError("MAXMBRS cannot be less than the existing member count.");
        if (memberSize is not null)
        {
            var size = memberSize.Apply(definition.MemberSize);
            command.CommandText = "SELECT coalesce(max(high_water),0) FROM sys_record_slots WHERE lib=$lib AND name=$name AND type='*FILE'";
            if (Convert.ToInt64(command.ExecuteScalar()) > size.Capacity) throw LogicalError("SIZE cannot be less than an existing member extent.");
            if(size!=(definition.MemberSize??new()))
            {
                command.CommandText="DELETE FROM sys_member_capacity WHERE member_id IN(SELECT id FROM sys_record_slots WHERE lib=$lib AND name=$name AND type='*FILE')";
                command.ExecuteNonQuery();
            }
            definition.MemberSize = size;
        }
        if (maximumMembers is not null) definition.MaximumMembers = maximumMembers.Value;
        if (recordLockWaitSeconds is not null) definition.RecordLockWaitSeconds = recordLockWaitSeconds.Value;
        if (reuseDeletedRecords is not null) definition.ReuseDeletedRecords = reuseDeletedRecords.Value;
        command.CommandText = """
            UPDATE sys_file_defs SET def=$definition WHERE lib=$lib AND name=$name AND type='*FILE';
            UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE';
            """;
        command.Parameters.AddWithValue("$definition", definition.ToJson()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit(); return definition.MaximumMembers;
    }

    private void AuthorizeFileManagement(string library, string name, AuthorityBit required = AuthorityBit.ObjectOperate)
    {
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireObjectAny(library, name, FileType,
            required, AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter);
        authorization.RequireFileDataState(library, name, required);
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
