using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;
using Ipc.Session.Sql;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class InteractiveSqlTests : IDisposable
{
    private readonly IpcSystem _system=IpcSystem.Create(":memory:");
    private readonly MenuController _menu;
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options=>new(SqlNaming.System,"QGPL","QSECOFR",["QGPL"]);
    public InteractiveSqlTests()
    {
        _system.Start();_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"InteractiveSqlFixture91");
        _files=new(_system.Connections,_system.Objects);_menu=new(_system,_system.Security.Profiles.Get("QSECOFR"));
    }
    public void Dispose() { _menu.Dispose();_system.Dispose(); }
    private string Screen=>string.Join('\n',Enumerable.Range(1,_menu.Buffer.Rows).Select(_menu.Buffer.RowText));
    private void Type(string text) { foreach(var character in text)_menu.Handle(new(AidKey.None,Character:character)); }
    private void Key(AidKey aid)=>_menu.Handle(new(aid));
    private void Edit(CursorEdit edit)=>_menu.Handle(new(AidKey.None,edit));
    private void Command(string text) { Type(text);Key(AidKey.Enter); }
    private void Exit(string choice) { Key(AidKey.Pf3);Type(choice);Key(AidKey.Enter); }
    private void Seed(int count=4)
    {
        _files.ExecuteSql("CREATE TABLE QGPL/DATA(ID INTEGER,NAME VARCHAR(20))",Options);
        for(var index=1;index<=count;index++)_files.ExecuteSql("INSERT INTO QGPL/DATA VALUES("+index+",'row"+index+"')",Options);
    }
    [Fact]
    public void STRSQL_opens_the_native_entry_screen_and_executes_DDL_DML_and_SELECT()
    {
        Command("STRSQL");Assert.Contains("Enter SQL Statements",Screen);
        Command("CREATE TABLE QGPL/DATA(ID INTEGER,NAME VARCHAR(20))");Assert.Contains("SQL statement completed",Screen);
        Command("INSERT INTO QGPL/DATA VALUES(7,'Ada')");Assert.Contains("1 rows affected",Screen);
        Command("SELECT ID,NAME FROM QGPL/DATA");Assert.Contains("Display Data",Screen);Assert.Contains("Ada",Screen);
        Key(AidKey.Pf12);Assert.Contains("Enter SQL Statements",Screen);
        Command("UPDATE QGPL/DATA SET NAME='Grace' WHERE ID=7");Assert.Contains("1 rows affected",Screen);
        Exit("2");Assert.Contains("AS/400 Main Menu",Screen);
        Assert.Equal("Grace",Assert.Single(_files.ReadAll("QGPL","DATA","DATA"))["NAME"]);
    }
    [Fact]
    public void Statement_errors_keep_the_editor_and_report_SQLSTATE_without_catalog_access()
    {
        Seed();Command("STRSQL");Command("SELECT BAD FROM QGPL/DATA");Assert.Contains("SQL0206",Screen);Assert.Contains("SQLSTATE=42703",Screen);
        Key(AidKey.Pf12);Command("SELECT ID FROM sys_jobs");Assert.Contains("SQL0551",Screen);
        Key(AidKey.Pf12);Command("SELECT ID FROM QGPL/DATA; DELETE FROM QGPL/DATA");Assert.Contains("SQL0104",Screen);
        Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));Exit("2");
    }
    [Theory]
    [InlineData("*ALWAYS")]
    [InlineData("*FORWARD")]
    public void Result_pages_scroll_in_both_directions_and_close_allocations(string refresh)
    {
        Seed(40);Command("STRSQL REFRESH("+refresh+")");Command("SELECT ID,NAME FROM QGPL/DATA ORDER BY ID");
        Assert.Contains("row1",Screen);Assert.Contains("More...",Screen);Key(AidKey.RollUp);Assert.Contains("row16",Screen);
        Key(AidKey.RollDown);Assert.Contains("row1",Screen);Key(AidKey.Pf12);
        Assert.DoesNotContain(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL","DATA",ObjectType.File)),item=>item.Lifetime=="Query");Exit("2");
    }
    [Fact]
    public void Saved_statements_are_retrieved_on_restart_and_discard_removes_the_saved_session()
    {
        Seed();Command("STRSQL");Command("SELECT ID FROM QGPL/DATA WHERE ID=2");Key(AidKey.Pf12);Exit("1");Assert.True(Screen.Contains("AS/400 Main Menu"),Screen);
        Command("STRSQL NAMING(*SQL)");Assert.True(Screen.Contains("parameters were ignored"),Screen);Key(AidKey.Pf9);Assert.Contains("SELECT ID FROM QGPL/DATA WHERE ID=2",Screen);
        Key(AidKey.Enter);Assert.Contains("Display Data",Screen);Key(AidKey.Pf12);Exit("2");
        Command("STRSQL NAMING(*SQL)");Key(AidKey.Pf9);Assert.DoesNotContain("SELECT ID FROM",Screen);Exit("2");
    }
    [Fact]
    public void Syntax_and_validation_modes_do_not_execute_statements()
    {
        Seed();Command("STRSQL PROCESS(*SYN)");Command("DELETE FROM QGPL/MISSING");Assert.Contains("syntax checked",Screen);Exit("2");
        Command("STRSQL PROCESS(*VLD)");Command("DELETE FROM QGPL/DATA");Assert.Contains("validated",Screen);
        Command("SELECT 1/0 FROM QGPL/DATA");Assert.Contains("validated",Screen);Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));Exit("2");
    }
    [Fact]
    public void Exit_resume_cancel_and_help_preserve_a_multiline_draft()
    {
        Command("STRSQL");Type("SELECT");Key(AidKey.Pf6);Type(" ID FROM QGPL/DATA");Key(AidKey.Pf1);Assert.Contains("Interactive SQL Help",Screen);Key(AidKey.Pf12);
        Assert.Contains("ID FROM QGPL/DATA",Screen);Key(AidKey.Pf3);Type("3");Key(AidKey.Enter);Assert.Contains("SELECT",Screen);
        Key(AidKey.Pf3);Key(AidKey.Pf12);Assert.Contains("ID FROM QGPL/DATA",Screen);Exit("2");
    }
    [Fact]
    public void A_terminal_is_required_and_unsupported_modes_reject_before_starting_a_session()
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var execution=new ExecutionSession(_system,job,CancellationToken.None);
        Assert.Equal("SQL6141",execution.Execute("STRSQL").MessageId);
        Command("STRSQL COMMIT(*CHG)");Assert.Contains("unfinished",Screen);Assert.DoesNotContain("Enter SQL Statements",Screen);
        Command("STRSQL REFRESH(*ALWAYS) ALWCPYDTA(*OPTIMIZE)");Assert.Contains("cannot use",Screen);
        Command("STRSQL SQL('SELECT ID FROM QGPL/DATA')");Assert.Contains("unsupported",Screen);
    }
    [Fact]
    public void Source_exit_exports_SQL_statements_through_the_shared_source_store()
    {
        Seed();_files.CreateSourceFile("QGPL","QSQLSRC",sourceWidth:240);
        Command("STRSQL");Command("UPDATE QGPL/DATA SET NAME='changed' WHERE ID=1");Key(AidKey.Pf3);Type("4");Key(AidKey.Enter);
        Assert.Contains("Save Session in Source File",Screen);Key(AidKey.Enter);
        var source=_files.ReadSourceMember("QGPL","QSQLSRC","SQLSESSION").Source;Assert.Contains("UPDATE QGPL/DATA",source);Assert.EndsWith(";",source.TrimEnd());
    }
    [Theory]
    [InlineData(24,80)]
    [InlineData(27,132)]
    public void SQL_screen_handles_long_lines_edit_keys_prompt_fields_and_both_geometries(int rows,int columns)
    {
        var screen=new InteractiveSqlScreen(new(),[],"",rows,columns);
        foreach(var c in "SELECT ID FROM QGPL/DATA WHERE NAME='ORDER BY'")screen.Handle(new(AidKey.None,Character:c));
        screen.Handle(new(AidKey.Pf4));Assert.Contains("Specify SELECT Statement",string.Join('\n',Enumerable.Range(1,rows).Select(screen.Buffer.RowText)));
        screen.Handle(new(AidKey.Enter));Assert.Contains("NAME='ORDER BY'",screen.Draft);
        screen.Handle(new(AidKey.Pf6));screen.Handle(new(AidKey.Pf10));Assert.True(screen.Draft.Contains('\n'));
    }
}
