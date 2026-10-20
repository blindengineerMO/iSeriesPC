using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class InteractiveSqlTests
{
    [Fact]
    public void Saved_query_results_page_refresh_and_return_without_opening_the_SQL_editor()
    {
        Seed(40); new QueryManagementStore(_system.Connections).Create("QGPL", "REPORT", "SELECT ID,NAME FROM QGPL/DATA ORDER BY ID");
        Command("STRQMQRY REPORT"); Assert.Contains("Display Data", Screen); Assert.Contains("row1", Screen);
        Key(AidKey.Pf1); Assert.Contains("Query Output Help", Screen); Assert.Contains("returns to the caller", Screen); Assert.DoesNotContain("statement entry", Screen);
        Key(AidKey.Pf12); Assert.Contains("Display Data", Screen);
        Key(AidKey.RollUp); Assert.Contains("row16", Screen); Key(AidKey.RollDown); Assert.Contains("row1", Screen);
        Key(AidKey.Pf5); Assert.Contains("Display Data", Screen); Key(AidKey.Pf12);
        Assert.Contains("AS/400 Main Menu", Screen); Assert.DoesNotContain("Enter SQL Statements", Screen);
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)), item => item.Job == _menu.JobKey);
    }
    [Fact]
    public void Missing_variables_prompt_in_source_order_and_blank_or_cancel_never_runs_the_query()
    {
        Seed(); new QueryManagementStore(_system.Connections).Create("QGPL", "CHANGE", "UPDATE QGPL/DATA SET NAME=&TEXT WHERE ID=&ID");
        Command("STRQMQRY CHANGE SETVAR((ID 1))"); Assert.Contains("Query Variable Value", Screen); Assert.Contains("TEXT", Screen);
        Type("'prompted'"); Key(AidKey.Enter); Assert.Contains("1 rows affected", Screen); Assert.Equal("prompted", _files.ReadAll("QGPL", "DATA", "DATA")[0]["NAME"]);
        Command("STRQMQRY CHANGE"); Assert.Contains("TEXT", Screen); Type("'unrun'"); Key(AidKey.Enter); Assert.Contains("ID", Screen);
        Key(AidKey.Pf12); Assert.Contains("prompting was canceled", Screen); Assert.Equal("prompted", _files.ReadAll("QGPL", "DATA", "DATA")[0]["NAME"]);
        Command("STRQMQRY CHANGE"); Key(AidKey.Enter); Assert.Contains("prompting was canceled", Screen);
    }
    [Fact]
    public void Mixed_case_names_require_a_prompt_and_BLANK_supplies_a_single_space()
    {
        Seed(); new QueryManagementStore(_system.Connections).Create("QGPL", "CHANGE", "UPDATE QGPL/DATA SET NAME=&Mixed&SPACE WHERE ID=1");
        Command("STRQMQRY CHANGE SETVAR((SPACE ' '))"); Assert.Contains("Mixed", Screen); Type("'mixed'"); Key(AidKey.Enter);
        Assert.Equal("mixed", _files.ReadAll("QGPL", "DATA", "DATA")[0]["NAME"]);
        new QueryManagementStore(_system.Connections).Create("QGPL", "BLANKQ", "UPDATE QGPL/DATA SET NAME='blank'&SPACE WHERE ID=1");
        Command("STRQMQRY BLANKQ"); Type("*BLANK"); Key(AidKey.Enter); Assert.Equal("blank", _files.ReadAll("QGPL", "DATA", "DATA")[0]["NAME"]);
    }
    [Fact]
    public void An_interactive_CL_caller_resumes_after_query_results_and_after_a_variable_escape_is_monitored()
    {
        Seed(); new QueryManagementStore(_system.Connections).Create("QGPL", "REPORT", "SELECT ID,NAME FROM QGPL/DATA");
        new QueryManagementStore(_system.Connections).Create("QGPL", "CHANGE", "DELETE FROM QGPL/DATA WHERE ID=&ID");
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "QRYCALL", "PGM\nSTRQMQRY REPORT\nSTRQMQRY CHANGE\nMONMSG MSGID(QWM2701) EXEC(RUNSQL SQL('INSERT INTO QGPL/DATA VALUES(99,''after'')') COMMIT(*NONE))\nENDPGM", null);
        Command("CRTCLPGM PGM(QGPL/QRYCALL) SRCFILE(QGPL/SOURCE)"); Command("CALL QGPL/QRYCALL"); Assert.Contains("Display Data", Screen);
        Key(AidKey.Pf3); Assert.Contains("Query Variable Value", Screen); Key(AidKey.Pf12);
        Assert.Contains("AS/400 Main Menu", Screen); Assert.Contains(_files.ReadAll("QGPL", "DATA", "DATA"), row => Equals(row["ID"], 99L));
    }
    [Theory]
    [InlineData("*QMQRY")]
    [InlineData("*FILE")]
    public void Revoked_query_or_record_authority_closes_cached_output_and_releases_allocations(string type)
    {
        Seed(); new QueryManagementStore(_system.Connections).Create("QGPL", "REPORT", "SELECT ID,NAME FROM QGPL/DATA");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "SavedQueryViewer384");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits);
        using var menu = new MenuController(_system, _system.Security.Profiles.Get("QUSER"));
        foreach (var c in "STRQMQRY REPORT") menu.Handle(new(AidKey.None, Character: c)); menu.Handle(new(AidKey.Enter));
        string View() => string.Join('\n', Enumerable.Range(1, 24).Select(menu.Buffer.RowText)); Assert.Contains("Display Data", View());
        _system.Security.Authority.Grant("QGPL", type == "*QMQRY" ? "REPORT" : "DATA", type, "QUSER", AuthorityBit.None);
        menu.Handle(new(AidKey.Pf24)); Assert.DoesNotContain("Display Data", View()); Assert.Contains(type == "*QMQRY" ? "CPF9802" : "SQL0551", View());
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)), item => item.Job == menu.JobKey);
    }
    [Fact]
    public void A_disconnected_query_display_releases_the_cursor_and_cache()
    {
        Seed(); new QueryManagementStore(_system.Connections).Create("QGPL", "REPORT", "SELECT ID FROM QGPL/DATA");
        Command("STRQMQRY REPORT"); Assert.Contains("Display Data", Screen); _menu.RequestDisconnect(); _menu.Dispose();
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)), item => item.Lifetime == "Query");
    }
}
