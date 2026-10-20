using Ipc.Core.Work;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class InquiryReplyMigrationTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ipc-inquiry-upgrade-"+Guid.NewGuid().ToString("N"));
    private static object? Sql(SqliteConnectionFactory factory,string sql)
    {using var connection=factory.Open();using var command=connection.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    [Fact]
    public void Upgrade_from_schema_32_is_atomic_and_preserves_legacy_jobs_with_required_reply_policy()
    {
        using var factory=new SqliteConnectionFactory(_directory);new Migrator(factory,Migrator.Migrations.Take(32).ToArray()).MigrateToLatest();
        Sql(factory,"INSERT INTO sys_objects(lib,name,type,owner,created,changed)VALUES('QUSRSYS','QPRINT','*OUTQ','QSYS','before','before'),('QSYS','QSYSOPR','*MSGQ','QSYS','before','before'); INSERT INTO sys_jobs(number,name,user,type,status,execution_state)VALUES(1,'LEGACY','QSECOFR','Interactive','Active','Running')");
        var failed=Migrator.Migrations[32]with{UpgradeData=(connection,transaction)=>{using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT * FROM missing_inquiry_upgrade";command.ExecuteScalar();}};
        Assert.Throws<SqliteException>(()=>new Migrator(factory,Migrator.Migrations.Take(32).Append(failed).ToArray()).MigrateToLatest());
        Assert.Equal(32,new Migrator(factory).CurrentVersion());Assert.Equal(0L,Sql(factory,"SELECT count(*) FROM pragma_table_info('sys_jobs') WHERE name='inquiry_reply'"));Assert.Equal(0L,Sql(factory,"SELECT count(*) FROM sqlite_schema WHERE name='sys_reply_list'"));
        new Migrator(factory).MigrateToLatest();Assert.Equal("*RQD",Sql(factory,"SELECT inquiry_reply FROM sys_jobs WHERE number=1"));Assert.Equal("LEGACY",Sql(factory,"SELECT name FROM sys_jobs WHERE number=1"));Assert.Equal("ok",Sql(factory,"PRAGMA integrity_check"));
    }
    [Fact]
    public void Reply_list_and_job_policy_survive_connection_factory_restart()
    {
        var key=new JobKey(1,"PERSIST","QSECOFR");
        using(var factory=new SqliteConnectionFactory(_directory))
        {
            new Migrator(factory).MigrateToLatest();Sql(factory,"INSERT INTO sys_objects(lib,name,type,owner,created,changed)VALUES('QUSRSYS','QPRINT','*OUTQ','QSYS','before','before'),('QSYS','QSYSOPR','*MSGQ','QSYS','before','before'); INSERT INTO sys_jobs(number,name,user,type,status,execution_state)VALUES(1,'PERSIST','QSECOFR','Interactive','Active','Running')");
            new InquiryReplyPolicy(factory).Set(key,"*SYSRPYL");new SystemReplyList(factory).Put(new(27,"CPA5305",[1,2,3],21,65535,"9999"));
        }
        using var reopened=new SqliteConnectionFactory(_directory);Assert.Equal("*SYSRPYL",new InquiryReplyPolicy(reopened).Get(key));
        var entry=Assert.Single(new SystemReplyList(reopened).List());Assert.Equal(27,entry.Sequence);Assert.Equal(21,entry.Start);Assert.Equal(new byte[]{1,2,3},entry.Comparison);Assert.Equal("9999",entry.Reply);
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
