using System.Text.Json;
using System.Text;
using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class JournalInitialization
{
    internal static void Capture(SqliteConnection connection, SqliteTransaction transaction, string library, string file,
        string member, long before, long after, bool deleted, string? defaultImage)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT b.id,b.journal_id,j.sequence,d.def FROM sys_journal_bindings b JOIN sys_journals j ON j.id=b.journal_id JOIN sys_file_defs d ON d.lib=b.library AND d.name=b.name AND d.type='*FILE' WHERE b.library=$lib AND b.name=$name";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", file);
        string binding, journal, definition;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return;
            binding = reader.GetString(0); journal = reader.GetString(1); definition = reader.GetString(3);
            if (reader.GetInt64(2) == long.MaxValue) throw new CpfException("IPC0210", "Journal sequence exhausted.");
        }
        var schema = JsonSerializer.Serialize(new { Version = 1,
            Columns = JournalHooks.Columns(connection, transaction, library + "." + file + "." + member), Definition = definition });
        if (Encoding.UTF8.GetByteCount(schema) > 1048576) throw new CpfException("IPC0210", "Initialization schema exceeds 1 MiB.");
        command.CommandText = """
            UPDATE sys_journals SET sequence=sequence+1 WHERE id=$journal;
            INSERT INTO sys_journal_entries(journal_id,sequence,receiver_id,code,entry_type,captured_at,principal,job,
                object_library,object_name,member,record_number,binding_id,user_data,schema_description)
            SELECT id,sequence,current_receiver,'F','IZ',ipc_journal_timestamp(),ipc_actor(),ipc_job(),
                $lib,$name,$member,$total,$binding,$data,$schema FROM sys_journals WHERE id=$journal;
            """;
        command.Parameters.AddWithValue("$schema", schema);
        command.Parameters.AddWithValue("$journal", journal); command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$total", after); command.Parameters.AddWithValue("$binding", binding);
        command.Parameters.AddWithValue("$data", JsonSerializer.SerializeToUtf8Bytes(new {
            Format = "IPCINIT2", BeforeExtent = before, AfterExtent = after, Count = after - before,
            Flag = deleted ? 1 : 0, DefaultImage = defaultImage }));
        command.ExecuteNonQuery();
    }
}
