using Ipc.Core.Messages;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class SqlSessionHistoryTests : IDisposable
{
    private readonly IpcSystem _system=IpcSystem.Create(":memory:");
    public SqlSessionHistoryTests()
    {
        _system.Start();foreach(var name in new[] { "QSECOFR","QUSER" })_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get(name),"SqlHistoryPassword214");
    }
    public void Dispose()=>_system.Dispose();
    [Fact]
    public void Saved_state_is_private_to_the_executing_profile_and_has_conflict_checked_revisions()
    {
        var store=new SqlSessionHistoryStore(_system.Connections);string revision;
        using(OperationIdentity.Enter("QSECOFR")) { revision=store.Save("{}",["SELECT 'private' FROM QGPL/DATA"],"draft",null);Assert.Equal("draft",store.Load()!.Draft); }
        using(OperationIdentity.Enter("QUSER")) { Assert.Null(store.Load());store.Save("{}",["different"],"",null); }
        using(OperationIdentity.Enter("QSECOFR"))
        {
            Assert.Equal(revision,store.Load()!.Revision);Assert.Equal("IPC0130",Assert.Throws<CpfException>(()=>store.Save("{}",[],"lost",null)).MessageId);
            var next=store.Save("{}",[],"changed",revision);Assert.NotEqual(revision,next);Assert.Throws<CpfException>(()=>store.Delete(revision));store.Delete(next);Assert.Null(store.Load());
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Audit_failure_rolls_back_a_saved_state_write_or_delete(bool delete)
    {
        using var identity=OperationIdentity.Enter("QSECOFR");var store=new SqlSessionHistoryStore(_system.Connections);var revision=store.Save("{}",["original"],"draft",null);
        using var connection=_system.Connections.Open();using var command=connection.CreateCommand();
        command.CommandText="CREATE TRIGGER fail_sql_history BEFORE INSERT ON sys_events WHEN NEW.kind='sql.session."+(delete ? "deleted" : "saved")+"' BEGIN SELECT RAISE(ABORT,'failure'); END";command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=> { if(delete)store.Delete(revision);else store.Save("{}",["rejected"],"changed",revision); });
        Assert.Equal("original",Assert.Single(store.Load()!.Statements));Assert.Equal(revision,store.Load()!.Revision);
        command.CommandText="DROP TRIGGER fail_sql_history";command.ExecuteNonQuery();store.Delete(revision);Assert.Null(store.Load());
    }
    [Fact]
    public void Saved_SQL_and_drafts_do_not_escape_into_audit_payloads()
    {
        using var identity=OperationIdentity.Enter("QSECOFR");var store=new SqlSessionHistoryStore(_system.Connections);store.Save("{}",["sensitive-sql"],"sensitive-draft",null);
        using var connection=_system.Connections.Open();using var command=connection.CreateCommand();command.CommandText="SELECT group_concat(payload) FROM sys_events";var events=command.ExecuteScalar()!.ToString()!;
        Assert.DoesNotContain("sensitive-sql",events);Assert.DoesNotContain("sensitive-draft",events);
    }
    [Fact]
    public void Invalid_or_oversized_saved_state_never_replaces_the_existing_revision()
    {
        using var identity=OperationIdentity.Enter("QSECOFR");var store=new SqlSessionHistoryStore(_system.Connections);var revision=store.Save("{}",[],"original",null);
        Assert.Throws<CpfException>(()=>store.Save("[]",[],"",revision));Assert.Throws<CpfException>(()=>store.Save("{}",Enumerable.Repeat("x",1001).ToArray(),"",revision));
        Assert.Throws<CpfException>(()=>store.Save("{}",[],new string('x',32769),revision));Assert.Equal("original",store.Load()!.Draft);
    }
    [Fact]
    public void Ended_jobs_or_foreign_job_contexts_cannot_save_a_session()
    {
        var first=_system.Jobs.CreateInteractive("QSECOFR");var foreign=_system.Jobs.CreateInteractive("QUSER");var store=new SqlSessionHistoryStore(_system.Connections);
        using(OperationIdentity.Enter("QSECOFR",foreign.Key))Assert.Throws<CpfException>(()=>store.Save("{}",[],"",null));
        _system.Jobs.Complete(first.Key,Ipc.Core.Work.JobCompletion.Normal);
        using(OperationIdentity.Enter("QSECOFR",first.Key))Assert.Throws<CpfException>(()=>store.Save("{}",[],"",null));
    }
}
