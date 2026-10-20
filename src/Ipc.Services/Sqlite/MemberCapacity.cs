using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class MemberCapacity
{
    internal static long Read(SqliteConnection connection,SqliteTransaction transaction,string id,string signature,long configured)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT capacity FROM sys_member_capacity WHERE member_id=$id AND definition_signature=$signature";
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$signature",signature);
        return command.ExecuteScalar() is long granted?Math.Max(configured,granted):configured;
    }
    internal static void Grant(SqliteConnection connection,SqliteTransaction transaction,string id,string signature,long capacity)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="INSERT INTO sys_member_capacity(member_id,definition_signature,capacity)VALUES($id,$signature,$capacity) ON CONFLICT(member_id)DO UPDATE SET definition_signature=excluded.definition_signature,capacity=excluded.capacity";
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$signature",signature);command.Parameters.AddWithValue("$capacity",capacity);command.ExecuteNonQuery();
    }
    internal static void Reset(SqliteConnection connection,SqliteTransaction transaction,string id)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="DELETE FROM sys_member_capacity WHERE member_id=$id";command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();
    }
    internal static void Copy(SqliteConnection connection,SqliteTransaction transaction,string sourceLibrary,string sourceFile,string targetLibrary,string targetFile)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            INSERT INTO sys_member_capacity(member_id,definition_signature,capacity)
            SELECT target.id,c.definition_signature,c.capacity FROM sys_member_capacity c
            JOIN sys_record_slots source ON source.id=c.member_id
            JOIN sys_record_slots target ON target.mbr=source.mbr AND target.type=source.type
            WHERE source.lib=$sl AND source.name=$sf AND target.lib=$tl AND target.name=$tf
            """;
        command.Parameters.AddWithValue("$sl",sourceLibrary);command.Parameters.AddWithValue("$sf",sourceFile);command.Parameters.AddWithValue("$tl",targetLibrary);command.Parameters.AddWithValue("$tf",targetFile);command.ExecuteNonQuery();
    }
}
