using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class MemberCapacityMigrationTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ipc-member-capacity-"+Guid.NewGuid().ToString("N"));
    private static object? Sql(SqliteConnectionFactory factory,string sql)
    {using var connection=factory.Open();using var command=connection.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static string Prepare(SqliteConnectionFactory factory)
    {
        new Migrator(factory,Migrator.Migrations.Take(33).ToArray()).MigrateToLatest();
        Sql(factory,"INSERT INTO sys_objects(lib,name,type,owner,created,changed)VALUES('QGPL','DATA','*FILE','QSECOFR','before','before'); INSERT INTO sys_file_members(lib,name,type,mbr,created)VALUES('QGPL','DATA','*FILE','DATA','before'); UPDATE sys_record_slots SET high_water=3");
        return (string)Sql(factory,"SELECT id FROM sys_record_slots")!;
    }
    [Fact]
    public void Failed_upgrade_preserves_old_member_identity_extent_and_history_before_retry()
    {
        using var factory=new SqliteConnectionFactory(_directory);var id=Prepare(factory);var history=Sql(factory,"SELECT group_concat(checksum) FROM sys_migrations");
        var failed=Migrator.Migrations[33]with{UpgradeData=(connection,transaction)=>{using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT * FROM missing_capacity_upgrade";command.ExecuteScalar();}};
        Assert.Throws<SqliteException>(()=>new Migrator(factory,Migrator.Migrations.Take(33).Append(failed).ToArray()).MigrateToLatest());
        Assert.Equal(33,new Migrator(factory).CurrentVersion());Assert.Equal(history,Sql(factory,"SELECT group_concat(checksum) FROM sys_migrations"));Assert.Equal(0L,Sql(factory,"SELECT count(*) FROM sqlite_schema WHERE name IN('sys_member_capacity','sys_member_size_waits','ipc_member_size_wait_end')"));
        new Migrator(factory).MigrateToLatest();Assert.Equal(id,Sql(factory,"SELECT id FROM sys_record_slots"));Assert.Equal(3L,Sql(factory,"SELECT high_water FROM sys_record_slots"));Assert.Equal(0L,Sql(factory,"SELECT count(*) FROM sys_member_capacity"));Assert.Equal("ok",Sql(factory,"PRAGMA integrity_check"));
    }
    [Fact]
    public void Granted_capacity_is_durable_configuration_specific_and_cascades_with_the_member()
    {
        string id;
        using(var factory=new SqliteConnectionFactory(_directory))
        {
            id=Prepare(factory);new Migrator(factory).MigrateToLatest();using var connection=factory.Open();using var transaction=connection.BeginTransaction();MemberCapacity.Grant(connection,transaction,id,"1/2/1/False",5);transaction.Commit();
        }
        using var reopened=new SqliteConnectionFactory(_directory);using(var connection=reopened.Open())using(var transaction=connection.BeginTransaction())
        {Assert.Equal(5,MemberCapacity.Read(connection,transaction,id,"1/2/1/False",3));Assert.Equal(3,MemberCapacity.Read(connection,transaction,id,"1/3/1/False",3));}
        Sql(reopened,"DELETE FROM sys_file_members WHERE name='DATA'");Assert.Equal(0L,Sql(reopened,"SELECT count(*) FROM sys_member_capacity"));
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
