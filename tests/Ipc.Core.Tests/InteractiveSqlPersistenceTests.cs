using Ipc.Console.Session;
using Ipc.Core.Catalog;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class InteractiveSqlPersistenceTests
{
    [Theory]
    [InlineData("*ALWAYS",true)]
    [InlineData("*FORWARD",false)]
    public void Refresh_modes_refresh_new_pages_and_distinguish_already_displayed_rows(string refresh,bool changedOnReturn)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ipc-strsql-refresh-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var system=IpcSystem.Create(directory);system.Start();system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"),"SqlRefreshFixture117");
            var files=new SqliteFileStore(system.Connections,system.Objects);var options=new SqlExecutionOptions(SqlNaming.System,"QGPL","QSECOFR",["QGPL"]);
            files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER,NAME CHAR(20))",options);
            for(var index=1;index<=40;index++)files.ExecuteSql("INSERT INTO QGPL/DATA VALUES("+index+",'row"+index+"')",options);
            using var menu=new MenuController(system,system.Security.Profiles.Get("QSECOFR"));
            void Command(string text) { foreach(var character in text)menu.Handle(new(AidKey.None,Character:character));menu.Handle(new(AidKey.Enter)); }
            string Screen()=>string.Join('\n',Enumerable.Range(1,24).Select(menu.Buffer.RowText));
            Command("STRSQL REFRESH("+refresh+")");Command("SELECT ID,NAME FROM QGPL/DATA");
            files.ExecuteSql("UPDATE QGPL/DATA SET NAME='changed' WHERE ID IN(1,16)",options);
            menu.Handle(new(AidKey.RollUp));Assert.Contains("changed",Screen());menu.Handle(new(AidKey.RollDown));Assert.Equal(changedOnReturn,Screen().Contains("changed"));
            menu.Handle(new(AidKey.Pf5));Assert.Contains("changed",Screen());menu.Handle(new(AidKey.Pf12));menu.Handle(new(AidKey.Pf3));menu.Handle(new(AidKey.None,Character:'2'));menu.Handle(new(AidKey.Enter));
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    [Fact]
    public void Saved_sessions_survive_restart_and_remain_separate_across_profiles()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ipc-strsql-save-"+Guid.NewGuid().ToString("N"));string revision;
        try
        {
            using(var system=IpcSystem.Create(directory))
            {
                system.Start();system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"),"SqlSavedSession159");
                using var identity=OperationIdentity.Enter("QSECOFR");revision=new SqlSessionHistoryStore(system.Connections).Save("{}",["SELECT ID FROM QGPL/DATA"],"unfinished draft",null);
            }
            using var restarted=IpcSystem.Create(directory);restarted.Start();
            using(OperationIdentity.Enter("QSECOFR")) { var saved=new SqlSessionHistoryStore(restarted.Connections).Load();Assert.Equal(revision,saved!.Revision);Assert.Equal("unfinished draft",saved.Draft); }
            restarted.Security.Profiles.SetPassword(restarted.Security.Profiles.Get("QUSER"),"SqlSavedUser129");using(OperationIdentity.Enter("QUSER"))Assert.Null(new SqlSessionHistoryStore(restarted.Connections).Load());
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    [Fact]
    public void Schema_34_upgrades_to_private_saved_session_storage_with_a_restorable_backup()
    {
        var directory=Path.Combine(Path.GetTempPath(),"ipc-strsql-migration-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var factory=new SqliteConnectionFactory(directory);new Migrator(factory,Migrator.Migrations.Take(34).ToArray()).MigrateToLatest();
            using(var connection=factory.Open())using(var command=connection.CreateCommand()) { command.CommandText="CREATE TABLE preserved_sql_data(value TEXT); INSERT INTO preserved_sql_data VALUES('preserved')";command.ExecuteNonQuery(); }
            var upgrade=new Migrator(factory);upgrade.MigrateToLatest();Assert.Equal(35,SystemCatalog.SchemaVersion);Assert.Equal(35,upgrade.CurrentVersion());Assert.NotNull(upgrade.LastBackupPath);
            using(var connection=factory.Open())using(var command=connection.CreateCommand()) { command.CommandText="SELECT value FROM preserved_sql_data";Assert.Equal("preserved",command.ExecuteScalar());command.CommandText="SELECT count(*) FROM sys_sql_sessions";Assert.Equal(0L,command.ExecuteScalar()); }
            using var backup=new SqliteConnectionFactory(directory,"backup.db");File.Copy(upgrade.LastBackupPath!,backup.DatabasePath!);Assert.Equal(34,new Migrator(backup).CurrentVersion());
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
}
