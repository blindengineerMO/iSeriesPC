using System.Text;
using System.Text.Json;
using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class JournalHooks
{
    internal static void ValidateRelocation(SqliteConnection connection, SqliteTransaction transaction, Ipc.Core.Objects.QualifiedName source, string type, bool copy, bool targetTemporary)
    {
        if (!Available(connection, transaction)) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$library", source.Library); command.Parameters.AddWithValue("$name", source.Name.Value);
        if (type is Ipc.Core.Objects.ObjectType.Journal or Ipc.Core.Objects.ObjectType.JournalReceiver)
        {
            command.CommandText = "SELECT count(*) FROM " + (type == Ipc.Core.Objects.ObjectType.Journal ? "sys_journals" : "sys_journal_receivers") + " WHERE library=$library AND name=$name";
            if ((copy || targetTemporary) && Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("Journal/receiver payloads cannot be duplicated or moved to QTEMP through generic object operations.");
        }
        if (type == Ipc.Core.Objects.ObjectType.File && !copy && targetTemporary)
        {
            command.CommandText = "SELECT count(*) FROM sys_journal_bindings WHERE library=$library AND name=$name";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("End journaling before moving the file to QTEMP; private journal bindings are not yet supported.");
        }
    }
    internal static bool Available(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='sys_journal_triggers'";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
    internal static bool OwnsTrigger(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        if (!Available(connection, transaction)) return false;
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT count(*) FROM sys_journal_triggers WHERE name=$name"; command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
    internal static void InstallMember(SqliteConnection connection, SqliteTransaction transaction, string library, string file, string member)
    {
        RecordSlots.InstallMember(connection, transaction, library, file, member);
        if (!Available(connection, transaction)) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,images FROM sys_journal_bindings WHERE library=$library AND name=$file";
        command.Parameters.AddWithValue("$library", library); command.Parameters.AddWithValue("$file", file);
        string binding; string images;
        using (var reader = command.ExecuteReader()) { if (!reader.Read()) return; binding = reader.GetString(0); images = reader.GetString(1); }
        var table = library + "." + file + "." + member;
        var columns = Columns(connection, transaction, table);
        command.CommandText = "SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$table"; command.Parameters.AddWithValue("$table", table);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("Journal capture requires a rowid physical member.");
        var rowid = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(alias => columns.All(column => !column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            ?? throw Invalid("All SQLite record-number aliases are shadowed by fields.");
        command.CommandText = "SELECT count(*) FROM " + Quote(table) + " WHERE " + Quote(rowid) + "<1";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("Journaling requires positive physical record numbers.");
        command.CommandText = "SELECT def FROM sys_file_defs WHERE lib=$library AND name=$file AND type='*FILE'";
        var definition = command.ExecuteScalar() as string ?? throw Invalid("Missing physical file definition.");
        var schema = JsonSerializer.Serialize(new { Version = 1, Columns = columns, Definition = definition });
        if (Encoding.UTF8.GetByteCount(schema) > 1048576) throw Invalid("Journal schema snapshots are limited to 1 MiB.");
        var expected = JsonSerializer.Serialize(columns.Select(column => new object[] { column.Name, column.Type, column.Hidden }).ToArray());
        var previous = Image("OLD", columns); var next = Image("NEW", columns);
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var trigger = "ipc_jrn_" + Guid.NewGuid().ToString("N");
            var rrn = (operation == "DELETE" ? "OLD." : "NEW.") + Quote(rowid);
            var body = operation == "UPDATE" ? Append(trigger, "UB", "OLD." + Quote(rowid), schema, previous, "NULL", images == "*BOTH" ? "1" : "ipc_commit_before()=1") : "";
            body += operation switch {
                "INSERT" => Append(trigger, "PT", rrn, schema, "NULL", next),
                "UPDATE" => Append(trigger, "UP", rrn, schema, images == "*BOTH" ? previous : "CASE WHEN ipc_commit_before()=1 THEN " + previous + " ELSE NULL END", next),
                _ => Append(trigger, "DL", rrn, schema, previous, "NULL") };
            var tableExpression = $"(SELECT library||'.'||file||'.'||member FROM sys_journal_triggers WHERE name={Literal(trigger)})";
            var actualColumns = $"SELECT cid,name,type,hidden FROM pragma_table_xinfo({tableExpression})";
            var expectedColumns = $"SELECT CAST(key AS INTEGER),json_extract(value,'$[0]'),json_extract(value,'$[1]'),json_extract(value,'$[2]') FROM json_each({Literal(expected)})";
            var guard = $"SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM sys_journal_triggers WHERE name={Literal(trigger)}) OR EXISTS({actualColumns} EXCEPT {expectedColumns}) OR EXISTS({expectedColumns} EXCEPT {actualColumns}) THEN RAISE(ABORT,'IPC0210: Journal registration/schema changed; end and restart journaling') END;";
            if (operation == "UPDATE") guard += $"SELECT CASE WHEN OLD.{Quote(rowid)}<>NEW.{Quote(rowid)} THEN RAISE(ABORT,'IPC0210: Journaled record numbers cannot change') END;";
            if (operation != "DELETE") guard += $"SELECT CASE WHEN {rrn}<1 THEN RAISE(ABORT,'IPC0210: Journal record number must be positive') END;";
            command.CommandText = $"CREATE TRIGGER {Quote(trigger)} AFTER {operation} ON {Quote(table)} WHEN ipc_journal_replay()=0 BEGIN {guard} {body} END";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO sys_journal_triggers(name,binding_id,library,file,member) VALUES($trigger,$binding,$library,$file,$member)";
            command.Parameters.AddWithValue("$trigger", trigger); command.Parameters.AddWithValue("$binding", binding); command.Parameters.AddWithValue("$member", member); command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$trigger"); command.Parameters.RemoveAt("$binding"); command.Parameters.RemoveAt("$member");
        }
    }
    internal static void DropBinding(SqliteConnection connection, SqliteTransaction transaction, string binding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT name FROM sys_journal_triggers WHERE binding_id=$id"; command.Parameters.AddWithValue("$id", binding);
        var triggers = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
        foreach (var name in triggers) { command.CommandText = "DROP TRIGGER IF EXISTS " + Quote(name); command.ExecuteNonQuery(); }
        command.CommandText = "DELETE FROM sys_journal_bindings WHERE id=$id"; command.ExecuteNonQuery();
    }
    internal static IReadOnlyList<Ipc.Services.Work.JournalColumn> Columns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT name,type,hidden FROM pragma_table_xinfo($table) ORDER BY cid"; command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader(); var columns = new List<Ipc.Services.Work.JournalColumn>();
        while (reader.Read()) columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        if (columns.Count == 0) throw Invalid("Physical member schema was not found."); return columns;
    }
    private static string Append(string trigger, string type, string rrn, string schema, string before, string after, string condition = "1") => $"""
        SELECT CASE WHEN (SELECT j.sequence FROM sys_journals j JOIN sys_journal_bindings b ON b.journal_id=j.id JOIN sys_journal_triggers t ON t.binding_id=b.id WHERE t.name={Literal(trigger)})>=9223372036854775807
            THEN RAISE(ABORT,'IPC0210: Journal sequence exhausted') END;
        UPDATE sys_journals SET sequence=sequence+1 WHERE ({condition}) AND id=(SELECT b.journal_id FROM sys_journal_bindings b JOIN sys_journal_triggers t ON t.binding_id=b.id WHERE t.name={Literal(trigger)});
        INSERT INTO sys_journal_entries(journal_id,sequence,receiver_id,code,entry_type,captured_at,principal,job,object_library,object_name,member,record_number,schema_description,before_image,after_image,binding_id,commit_cycle)
        SELECT j.id,j.sequence,j.current_receiver,'R',{Literal(type)},ipc_journal_timestamp(),ipc_actor(),ipc_job(),t.library,t.file,t.member,{rrn},{Literal(schema)},{before},{after},b.id,ipc_commit_cycle()
        FROM sys_journals j JOIN sys_journal_bindings b ON b.journal_id=j.id JOIN sys_journal_triggers t ON t.binding_id=b.id WHERE t.name={Literal(trigger)} AND ({condition});
        """;
    internal static string Image(string row, IReadOnlyList<Ipc.Services.Work.JournalColumn> columns)
    {
        var cells = columns.Select(column => {
            var value = row + "." + Quote(column.Name);
            return $"json_array({Literal(column.Name)},typeof({value}),CASE typeof({value}) WHEN 'blob' THEN hex({value}) WHEN 'integer' THEN CAST({value} AS TEXT) WHEN 'real' THEN ipc_journal_real({value}) ELSE {value} END)";
        }).ToArray();
        static string Join(IReadOnlyList<string> parts, int from, int count) => count == 1 ? parts[from] : "(" + Join(parts, from, count / 2) + "||','||" + Join(parts, from + count / 2, count - count / 2) + ")";
        return "('['||" + Join(cells, 0, cells.Length) + "||']')";
    }
    internal static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static CpfException Invalid(string text) => new("IPC0210", text);
}
