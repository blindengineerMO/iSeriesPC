using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class RecordSlotMigration
{
    internal const string Sql = """
        CREATE TABLE sys_record_slots (
            id TEXT PRIMARY KEY, lib TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, mbr TEXT NOT NULL,
            high_water INTEGER NOT NULL DEFAULT 0 CHECK(high_water>=0),
            UNIQUE(lib,name,type,mbr),
            FOREIGN KEY(lib,name,type,mbr) REFERENCES sys_file_members(lib,name,type,mbr) ON UPDATE CASCADE ON DELETE CASCADE
        );
        CREATE TABLE sys_record_versions (
            member_id TEXT NOT NULL REFERENCES sys_record_slots(id) ON DELETE CASCADE,
            rrn INTEGER NOT NULL, version TEXT NOT NULL, PRIMARY KEY(member_id,rrn)
        );
        CREATE TABLE sys_record_slot_triggers (
            name TEXT PRIMARY KEY, member_id TEXT NOT NULL REFERENCES sys_record_slots(id) ON DELETE CASCADE
        );
        CREATE TRIGGER ipc_record_member_identity AFTER INSERT ON sys_file_members BEGIN
            INSERT INTO sys_record_slots(id,lib,name,type,mbr) VALUES(lower(hex(randomblob(16))),NEW.lib,NEW.name,NEW.type,NEW.mbr);
        END;
        INSERT INTO sys_record_slots(id,lib,name,type,mbr)
            SELECT lower(hex(randomblob(16))),lib,name,type,mbr FROM sys_file_members;
        """;
    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT m.lib,m.name,m.mbr FROM sys_file_members m JOIN sqlite_schema s ON s.type='table' AND s.name=m.lib||'.'||m.name||'.'||m.mbr";
        var members = new List<(string Library, string File, string Member)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) members.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        foreach (var member in members) RecordSlots.InstallMember(connection, transaction, member.Library, member.File, member.Member);
    }
}
