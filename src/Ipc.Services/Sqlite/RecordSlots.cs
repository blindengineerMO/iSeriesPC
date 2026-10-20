using Ipc.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Durable member extents and record incarnations, independent of active row count.</summary>
internal static class RecordSlots
{
    internal const long MaximumNumber = 4294967288L;
    private static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    internal static bool Available(SqliteConnection connection, SqliteTransaction? transaction)
    { using var command = Command(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE name='sys_record_slots'"); return Convert.ToInt64(command.ExecuteScalar()) != 0; }
    internal static bool OwnsTrigger(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        if (!Available(connection, transaction)) return false;
        using var command = Command(connection, transaction, "SELECT count(*) FROM sys_record_slot_triggers WHERE name=$name"); command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
    internal static string? Identity(SqliteConnection connection, SqliteTransaction? transaction, string library, string file, string member)
    {
        using var command = Command(connection, transaction, "SELECT id FROM sys_record_slots WHERE lib=$lib AND name=$file AND type='*FILE' AND mbr=$member");
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$file", file); command.Parameters.AddWithValue("$member", member);
        return command.ExecuteScalar() as string;
    }
    internal static void InstallMember(SqliteConnection connection, SqliteTransaction transaction, string library, string file, string member)
    {
        if (!Available(connection, transaction)) return;
        var table = library + "." + file + "." + member;
        var id = Identity(connection, transaction, library, file, member) ?? throw new CpfException("IPC0140", "Missing record member identity.");
        using (var layout = Command(connection, transaction, "SELECT wr FROM pragma_table_list WHERE schema='main' AND name=$table"))
        {
            layout.Parameters.AddWithValue("$table", table);
            if (Convert.ToInt64(layout.ExecuteScalar()) != 0) return; // Generic object copies also support WITHOUT ROWID tables.
        }
        var columns = JournalHooks.Columns(connection, transaction, table);
        var rowid = new[] { "_rowid_", "rowid", "oid" }.FirstOrDefault(alias => columns.All(c => !c.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)));
        if (rowid is null) return; // Existing data can hide every rowid alias; native RRN access rejects that format.
        using var command = Command(connection, transaction, "SELECT count(*) FROM sys_record_slot_triggers t JOIN sqlite_schema s ON s.type='trigger' AND s.name=t.name WHERE member_id=$id");
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(command.ExecuteScalar()) == 3) return;
        command.CommandText = "SELECT name FROM sys_record_slot_triggers WHERE member_id=$id";
        var previous = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) previous.Add(reader.GetString(0));
        foreach (var name in previous) { command.CommandText = "DROP TRIGGER IF EXISTS " + Quote(name); command.ExecuteNonQuery(); }
        command.CommandText = "DELETE FROM sys_record_slot_triggers WHERE member_id=$id"; command.ExecuteNonQuery();
        command.CommandText = $"UPDATE sys_record_slots SET high_water=max(high_water,coalesce((SELECT max({Quote(rowid)}) FROM {Quote(table)}),0)) WHERE id=$id"; command.ExecuteNonQuery();
        command.CommandText = $"INSERT OR IGNORE INTO sys_record_versions(member_id,rrn,version) SELECT $id,{Quote(rowid)},lower(hex(randomblob(16))) FROM {Quote(table)}"; command.ExecuteNonQuery();
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var trigger = "ipc_rrn_" + Guid.NewGuid().ToString("N"); var source = operation == "DELETE" ? "OLD" : "NEW";
            var number = source + "." + Quote(rowid);
            var body = $"INSERT INTO sys_record_versions(member_id,rrn,version) VALUES({Literal(id)},{number},lower(hex(randomblob(16)))) ON CONFLICT(member_id,rrn) DO UPDATE SET version=excluded.version;";
            if (operation != "DELETE") body += $" UPDATE sys_record_slots SET high_water=max(high_water,{number}) WHERE id={Literal(id)};";
            if (operation == "UPDATE") body += $" INSERT INTO sys_record_versions(member_id,rrn,version) SELECT {Literal(id)},OLD.{Quote(rowid)},lower(hex(randomblob(16))) WHERE OLD.{Quote(rowid)}<>NEW.{Quote(rowid)} ON CONFLICT(member_id,rrn) DO UPDATE SET version=excluded.version;";
            command.CommandText = $"CREATE TRIGGER {Quote(trigger)} AFTER {operation} ON {Quote(table)} BEGIN {body} END"; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO sys_record_slot_triggers(name,member_id) VALUES($trigger,$id)"; command.Parameters.AddWithValue("$trigger",trigger); command.ExecuteNonQuery(); command.Parameters.RemoveAt("$trigger");
        }
    }
    internal static long HighWater(SqliteConnection connection, SqliteTransaction? transaction, string id)
    { using var command = Command(connection, transaction, "SELECT high_water FROM sys_record_slots WHERE id=$id"); command.Parameters.AddWithValue("$id",id); return Convert.ToInt64(command.ExecuteScalar()); }
    internal static void Reserve(SqliteConnection connection, SqliteTransaction transaction, string id, long total)
    {
        if (total is < 0 or > MaximumNumber) throw new CpfException("CPF5030", "Relative record number is outside the physical-file range.");
        using var command = Command(connection,transaction,"UPDATE sys_record_slots SET high_water=max(high_water,$total) WHERE id=$id");
        command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$total",total);
        if (command.ExecuteNonQuery()!=1) throw new CpfException("IPC0140", "Record member identity changed.");
    }
    internal static void Reset(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(connection, transaction, "DELETE FROM sys_record_versions WHERE member_id=$id; UPDATE sys_record_slots SET high_water=0 WHERE id=$id"); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    internal static string VersionExpression(string library, string file, string member, string rrn)
        => $"(SELECT v.version FROM sys_record_versions v JOIN sys_record_slots s ON s.id=v.member_id WHERE s.lib={Literal(library)} AND s.name={Literal(file)} AND s.type='*FILE' AND s.mbr={Literal(member)} AND v.rrn={rrn})";
    internal static long Next(SqliteConnection connection, SqliteTransaction transaction, string library, string file, string member, string rowid, bool reuse)
    {
        var id = Identity(connection, transaction, library, file, member) ?? throw new CpfException("IPC0140", "Record member identity is unavailable.");
        var high = HighWater(connection,transaction,id); var table = Quote(library+"."+file+"."+member); var column = Quote(rowid);
        if (reuse && high > 0)
        {
            using var find = Command(connection,transaction,$"SELECT min(n) FROM (SELECT 1 AS n WHERE NOT EXISTS(SELECT 1 FROM {table} WHERE {column}=1) UNION ALL SELECT p.{column}+1 FROM {table} p WHERE p.{column}>0 AND p.{column}<$high AND NOT EXISTS(SELECT 1 FROM {table} q WHERE q.{column}=p.{column}+1))");
            find.Parameters.AddWithValue("$high",high); if (find.ExecuteScalar() is long available) return available;
        }
        if (high >= MaximumNumber) throw new CpfException("CPF5030", "The physical member has no further relative record positions.");
        return high+1;
    }
    internal static void CopyState(SqliteConnection connection, SqliteTransaction transaction, string sourceLibrary, string sourceFile, string targetLibrary, string targetFile)
    {
        if (!Available(connection,transaction)) return;
        using var command = Command(connection,transaction,"SELECT t.mbr,s.high_water FROM sys_record_slots t JOIN sys_record_slots s ON s.lib=$sourceLib AND s.name=$sourceFile AND s.type=t.type AND s.mbr=t.mbr JOIN sqlite_schema x ON x.type='table' AND x.name=t.lib||'.'||t.name||'.'||t.mbr WHERE t.lib=$targetLib AND t.name=$targetFile AND t.type='*FILE'");
        command.Parameters.AddWithValue("$sourceLib",sourceLibrary); command.Parameters.AddWithValue("$sourceFile",sourceFile); command.Parameters.AddWithValue("$targetLib",targetLibrary); command.Parameters.AddWithValue("$targetFile",targetFile);
        var members=new List<(string Member,long High)>(); using(var reader=command.ExecuteReader())while(reader.Read())members.Add((reader.GetString(0),reader.GetInt64(1)));
        foreach(var item in members)
        { InstallMember(connection,transaction,targetLibrary,targetFile,item.Member); var id=Identity(connection,transaction,targetLibrary,targetFile,item.Member)!; Reserve(connection,transaction,id,item.High); }
        MemberCapacity.Copy(connection,transaction,sourceLibrary,sourceFile,targetLibrary,targetFile);
    }
}
