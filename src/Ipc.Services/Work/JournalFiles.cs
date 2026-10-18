using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class JournalStore
{
    public void StartFile(QualifiedName file, QualifiedName journal, string images = "*AFTER", CancellationToken cancellationToken = default)
    {
        Permanent(file); Require(journal, ObjectType.Journal, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        Require(file, ObjectType.File, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        if (images is not ("*AFTER" or "*BOTH")) throw Invalid("IMAGES requires *AFTER or *BOTH.");
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken); var target = InspectCore(connection, transaction, journal);
        using var command = Command(connection, transaction, "SELECT attribute FROM sys_objects WHERE lib=$library AND name=$name AND type='*FILE'"); Bind(command, file);
        if (command.ExecuteScalar() as string is not ("*PF" or "*SRCPF")) throw Invalid("STRJRNPF requires a physical data/source file.");
        command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$library AND name=$name AND type='*FILE'";
        if (command.ExecuteScalar() is not string) throw Invalid("Physical file definition was not found.");
        command.CommandText = "SELECT count(*) FROM sys_journal_bindings WHERE library=$library AND name=$name";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("File is already journaled.");
        var id = Guid.NewGuid().ToString("N"); command.CommandText = "INSERT INTO sys_journal_bindings(id,journal_id,library,name,images) VALUES($id,$journal,$library,$name,$images)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$journal", target.Id); command.Parameters.AddWithValue("$images", images); command.ExecuteNonQuery();
        command.CommandText = "SELECT mbr FROM sys_file_members WHERE lib=$library AND name=$name AND type='*FILE' ORDER BY mbr";
        var members = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) members.Add(reader.GetString(0));
        foreach (var member in members) { cancellationToken.ThrowIfCancellationRequested(); Ipc.Core.Work.JobExecutionBudget.Checkpoint(); JournalHooks.InstallMember(connection, transaction, file.Library, file.Name.Value, member); }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    public void EndFile(QualifiedName file, CancellationToken cancellationToken = default)
    {
        Require(file, ObjectType.File, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        using var command = Command(connection, transaction, "SELECT b.id,j.library,j.name FROM sys_journal_bindings b JOIN sys_journals j ON j.id=b.journal_id WHERE b.library=$library AND b.name=$name"); Bind(command, file);
        string id; QualifiedName journal;
        using (var reader = command.ExecuteReader()) { if (!reader.Read()) throw Invalid("File is not journaled."); id = reader.GetString(0); journal = new(reader.GetString(1), reader.GetString(2)); }
        Require(journal, ObjectType.Journal, AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        JournalHooks.DropBinding(connection, transaction, id); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
}
