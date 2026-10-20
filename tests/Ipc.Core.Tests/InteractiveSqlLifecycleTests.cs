using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session.Sql;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class InteractiveSqlTests
{
    [Fact]
    public void F4_command_prompt_starts_SQL_on_the_suspended_program_worker()
    {
        Type("STRSQL");Key(AidKey.Pf4);Assert.Contains("Prompt",Screen);Key(AidKey.Enter);Assert.Contains("Enter SQL Statements",Screen);Exit("2");Assert.Contains("AS/400 Main Menu",Screen);
    }
    [Fact]
    public void Interactive_CL_callers_resume_after_STRSQL_returns()
    {
        Seed();_files.CreateSourceFile("QGPL","SOURCE",sourceWidth:240);
        _files.SaveSourceMember("QGPL","SOURCE","SQLCALL","PGM\nSTRSQL\nRUNSQL SQL('INSERT INTO QGPL/DATA VALUES(99,''after'')') COMMIT(*NONE)\nENDPGM",null);
        Command("CRTCLPGM PGM(QGPL/SQLCALL) SRCFILE(QGPL/SOURCE)");Command("CALL QGPL/SQLCALL");Assert.Contains("Enter SQL Statements",Screen);
        Command("DELETE FROM QGPL/DATA WHERE ID=1");Exit("2");Assert.Contains(_files.ReadAll("QGPL","DATA","DATA"),row=>Equals(row["ID"],99L));
    }
    [Fact]
    public void Completed_statements_and_fetch_authority_checks_do_not_retain_automatic_file_locks()
    {
        Seed(20);Command("STRSQL");Command("UPDATE QGPL/DATA SET NAME='first' WHERE ID=1");
        var job=_system.Jobs.CreateInteractive("QSECOFR");var locks=new JobLockStore(_system.Connections);
        using(locks.Acquire(job.Key,new("QGPL","DATA",ObjectType.File),JobLockMode.Exclusive,TimeSpan.Zero)) { }
        Command("SELECT ID FROM QGPL/DATA");Key(AidKey.Pf24);Key(AidKey.Pf12);
        using(locks.Acquire(job.Key,new("QGPL","DATA",ObjectType.File),JobLockMode.Exclusive,TimeSpan.Zero)) { }Exit("2");
    }
    [Fact]
    public void F13_changes_attributes_atomically_and_keeps_bad_options_editable()
    {
        Command("STRSQL");Key(AidKey.Pf13);Key(AidKey.Enter);Assert.Contains("Change Session Attributes",Screen);
        Type("*SQL");Edit(CursorEdit.FieldEraseToEnd);Key(AidKey.Enter);Assert.Contains("attributes changed",Screen);
        Command("CREATE TABLE QGPL.DATA(ID INTEGER)");Assert.True(_files.FileExists("QGPL","DATA"));
        Key(AidKey.Pf13);Key(AidKey.Enter);Edit(CursorEdit.NextField);Type("*CHG");Edit(CursorEdit.FieldEraseToEnd);Key(AidKey.Enter);
        Assert.Contains("SQL0104",Screen);Assert.Contains("Change Session Attributes",Screen);Key(AidKey.Pf1);Assert.Contains("unfinished",Screen);Key(AidKey.Pf12);Key(AidKey.Pf12);Exit("2");
    }
    [Fact]
    public void Bracketed_paste_preserves_SQL_line_comments_without_running_the_statement()
    {
        Seed();Command("STRSQL");var parser=new TerminalParser();
        foreach(var character in "\u001b[200~SELECT ID --comment\r\nFROM QGPL/DATA\u001b[201~")foreach(var key in parser.Feed(character))_menu.Handle(key);
        Assert.Contains("Enter SQL Statements",Screen);Assert.Contains("FROM QGPL/DATA",Screen);Key(AidKey.Enter);Assert.Contains("Display Data",Screen);Key(AidKey.Pf12);Exit("2");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Result_scroll_caches_are_removed_before_job_or_factory_cleanup(bool shutdown)
    {
        Seed();var job=_system.Jobs.CreateInteractive("QSECOFR");InteractiveSqlResults results;
        using(OperationIdentity.Enter("QSECOFR",job.Key))
        {
            results=new(_system.Connections,_files,"SELECT ID FROM QGPL/DATA",Options,new(new(),"*YMD","/",":",false),CancellationToken.None);
            Assert.NotEmpty(results.Read(0,2).Rows);
        }
        var directory=results.DirectoryPath;Assert.True(Directory.Exists(directory));
        if(shutdown) { _menu.Dispose();_system.Connections.Dispose(); }else _system.Jobs.Complete(job.Key,JobCompletion.Normal);
        Assert.False(Directory.Exists(directory));Assert.ThrowsAny<OperationCanceledException>(()=>results.CheckAccess());results.Dispose();
    }
    [Fact]
    public void Result_headings_scroll_horizontally_and_zero_row_queries_still_show_metadata()
    {
        Seed();Command("STRSQL");Command("SELECT CAST(NAME AS CHAR(120)) AS LONGNAME,ID AS LAST FROM QGPL/DATA");
        Key(AidKey.Pf20);Key(AidKey.Pf20);Assert.Contains("LAST",Screen);Key(AidKey.Pf12);
        Command("SELECT ID,NAME FROM QGPL/DATA WHERE 1=0");Assert.Contains("ID",Screen);Assert.Contains("NAME",Screen);Assert.Contains("Bottom",Screen);Key(AidKey.Pf12);Exit("2");
    }
    [Fact]
    public void A_conflicting_saved_session_can_be_discarded_without_deleting_newer_state()
    {
        Command("STRSQL");Type("SELECT FIRST");
        using var other=new Ipc.Console.Session.MenuController(_system,_system.Security.Profiles.Get("QSECOFR"));
        foreach(var character in "STRSQL")other.Handle(new(AidKey.None,Character:character));other.Handle(new(AidKey.Enter));
        foreach(var character in "SELECT OTHER")other.Handle(new(AidKey.None,Character:character));other.Handle(new(AidKey.Pf3));other.Handle(new(AidKey.Enter));
        Exit("1");Assert.Contains("IPC0130",Screen);Type("2");Key(AidKey.Enter);Assert.Contains("newer saved session",Screen);
        using var identity=OperationIdentity.Enter("QSECOFR");Assert.Equal("SELECT OTHER",new SqlSessionHistoryStore(_system.Connections).Load()!.Draft);
    }

}
