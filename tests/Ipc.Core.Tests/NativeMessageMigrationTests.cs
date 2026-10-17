using Ipc.Core.Work;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class NativeMessageMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-native-message-migration-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    public NativeMessageMigrationTests()
    {
        _factory = new(_directory); new Migrator(_factory, Migrator.Migrations.Take(26).ToArray()).MigrateToLatest();
        using var connection = _factory.Open(); using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QGPL','INBOX','*MSGQ','QSYS','2026-10-17T00:00:00Z','2026-10-17T00:00:00Z');
            INSERT INTO sys_objects(lib,name,type,owner,created,changed) VALUES('QGPL','REPLIES','*MSGQ','QSYS','2026-10-17T00:00:00Z','2026-10-17T00:00:00Z');
            INSERT INTO sys_message_entries(key,queue_lib,queue_name,kind,message_id,severity,data,ccsid,sender,sender_job,sent,seen,replied,
                reply_lib,reply_name,reply_type,default_reply,correlation_key,exception_handled,reply_type_code,default_reply_code,
                sender_job_name,sender_job_user,sender_program,recipient_program,origin_sent,predefined)
            VALUES(2147548929,'QGPL','INBOX','INQ','ERR0001',40,X'E58193A48540C1C2C3',37,'QUSER',42,'2026-10-17T12:34:56Z',1,0,
                'QGPL','REPLIES','*MSGQ',X'D5',123,1,'22','23','SENDER','QUSER','PROGRAM','RECIPIENT','2026-10-17T12:00:00Z',$definition);
            INSERT INTO sys_message_entries(key,queue_lib,queue_name,kind,severity,data,ccsid,sender,sent)
                VALUES(2147549000,'QGPL','INBOX','INFO',0,X'',37,'QSYS','2026-10-17T00:00:00Z');
            DELETE FROM sys_message_entries WHERE key=2147549000;
            """;
        var definition = new PredefinedMessage("QGPL", "ERRORS", "*LIBL", DateTimeOffset.Parse("2026-10-17T00:00:00Z"),
            new("Value &1", "Help &1", 40, [new("*CHAR", 3)], 37, "N"), new ProgramBuffer([0xC1, 0xC2, 0xC3], 37));
        insert.Parameters.AddWithValue("$definition", definition.Serialize()); insert.ExecuteNonQuery();
    }
    [Fact]
    public void Upgrade_preserves_all_message_columns_foreign_keys_and_deleted_key_high_water_mark()
    {
        var before = Snapshot(); var upgrade = new Migrator(_factory, Migrator.Migrations.Take(27).ToArray()); upgrade.MigrateToLatest();
        Assert.Equal(27, upgrade.CurrentVersion()); Assert.Equal(before, Snapshot()); Assert.NotNull(upgrade.LastBackupPath);
        using var connection = _factory.Open(); using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent) VALUES('QGPL','INBOX','INFO',0,zeroblob(6000),37,'QSYS','2026-10-17T00:00:00Z') RETURNING key";
        Assert.True((long)insert.ExecuteScalar()! > 2147549000L);
        insert.CommandText = "PRAGMA foreign_key_check"; using (var reader = insert.ExecuteReader()) Assert.False(reader.Read());
        insert.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", insert.ExecuteScalar());
        insert.CommandText = "DELETE FROM sys_objects WHERE lib='QGPL' AND name='REPLIES' AND type='*MSGQ'"; insert.ExecuteNonQuery();
        insert.CommandText = "SELECT reply_lib IS NULL AND reply_name IS NULL AND reply_type IS NULL FROM sys_message_entries WHERE key=2147548929";
        Assert.Equal(1L, insert.ExecuteScalar());
    }
    [Fact]
    public void Empty_queue_upgrade_does_not_reuse_previously_deleted_keys()
    {
        using (var connection = _factory.Open())
        using (var delete = connection.CreateCommand())
        { delete.CommandText = "DELETE FROM sys_message_entries"; delete.ExecuteNonQuery(); }
        new Migrator(_factory).MigrateToLatest();
        using var upgraded = _factory.Open(); using var insert = upgraded.CreateCommand();
        insert.CommandText = "INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent) VALUES('QGPL','INBOX','INFO',0,X'',37,'QSYS','2026-10-17T00:00:00Z') RETURNING key";
        Assert.Equal(2147549001L, insert.ExecuteScalar());
    }
    [Fact]
    public void Failed_upgrade_rolls_back_the_rebuild_data_sequence_indexes_and_receiver_table()
    {
        var before = Snapshot(); var failing = new SchemaMigration(27, "Injected native message failure", [NativeMessagePayloadMigration.Sql, "SELECT * FROM missing_native_migration_table"]);
        var migrations = Migrator.Migrations.Take(26).Append(failing).ToArray();
        Assert.Throws<SqliteException>(() => new Migrator(_factory, migrations).MigrateToLatest());
        Assert.Equal(26, new Migrator(_factory).CurrentVersion()); Assert.Equal(before, Snapshot());
        using var connection = _factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='sys_message_receivers'"; Assert.Equal(0L, query.ExecuteScalar());
        query.CommandText = "INSERT INTO sys_message_entries(queue_lib,queue_name,kind,severity,data,ccsid,sender,sent) VALUES('QGPL','INBOX','INFO',0,zeroblob(6000),37,'QSYS','2026-10-17T00:00:00Z')";
        Assert.Throws<SqliteException>(() => query.ExecuteNonQuery());
        query.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='sys_message_entries'"; Assert.Equal(2147549000L, query.ExecuteScalar());
        new Migrator(_factory).MigrateToLatest(); Assert.Equal(before, Snapshot());
    }
    private string Snapshot()
    {
        using var connection = _factory.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT * FROM sys_message_entries ORDER BY key";
        using var reader = query.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) for (var n = 0; n < reader.FieldCount; n++) result.Add(reader.GetName(n) + "=" + (reader.GetValue(n) is byte[] bytes ? Convert.ToHexString(bytes) : reader.GetValue(n).ToString()));
        return string.Join("\n", result);
    }
    public void Dispose() { _factory.Dispose(); SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
