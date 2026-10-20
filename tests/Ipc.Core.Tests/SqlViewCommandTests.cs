using Ipc.Console.Session;
using Ipc.Db.Sql;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewTests
{
    private void Ok(string command)
    { var result = _session.Execute(command); Assert.False(result.IsError, result.Message); }
    [Fact]
    public void Scripts_saved_queries_and_query_outfiles_share_live_view_results()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "VIEWS", "CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID>1;", null);
        Ok("RUNSQLSTM QGPL/SOURCE VIEWS COMMIT(*NONE) OPTION(*NOLIST) PROCESS(*SYN)"); Assert.False(_files.FileExists("QGPL", "REPORT"));
        Ok("RUNSQLSTM QGPL/SOURCE VIEWS COMMIT(*NONE) OPTION(*NOLIST)");
        _files.SaveSourceMember("QGPL", "SOURCE", "QUERY", "SELECT ID,AMOUNT FROM QGPL/REPORT ORDER BY ID", null);
        Ok("CRTQMQRY QGPL/QUERY SRCFILE(QGPL/SOURCE)"); Ok("STRQMQRY QGPL/QUERY OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)");
        Assert.Equal(new long[] { 2, 3 }, _files.ReadAll("QGPL", "OUT", "OUT").Select(row => (long)row["ID"]!));
        Ok("RUNSQL SQL('DROP VIEW QGPL/REPORT') COMMIT(*NONE)"); Assert.False(_files.FileExists("QGPL", "REPORT"));
    }
    [Fact]
    public void Scripts_and_saved_query_writes_enforce_persisted_view_checks()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "VIEWS", "CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<10 WITH CHECK OPTION;\nINSERT INTO QGPL/REPORT VALUES(4,40);\nUPDATE QGPL/REPORT SET AMOUNT=50 WHERE ID=4;", null);
        Ok("RUNSQLSTM QGPL/SOURCE VIEWS COMMIT(*NONE) OPTION(*NOLIST) MARGINS(240)");
        _files.SaveSourceMember("QGPL", "SOURCE", "QUERY", "DELETE FROM QGPL/REPORT WHERE ID=4", null);
        Ok("CRTQMQRY QGPL/QUERY SRCFILE(QGPL/SOURCE)"); Ok("STRQMQRY QGPL/QUERY");
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        var denied = _session.Execute("RUNSQL SQL('INSERT INTO QGPL/REPORT VALUES(10,1)') COMMIT(*NONE)");
        Assert.True(denied.IsError); Assert.Contains("SQL0161", denied.Message);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiled_CL_and_RPG_consume_SQL_view_external_records(bool rpg)
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CAST(AMOUNT AS DECIMAL(10,2)) AS AMOUNT FROM QGPL/DATA RCDFMT RPTREC");
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "VIEWPGM", rpg ? """
            **free
            dcl-f REPORT extdesc('QGPL/REPORT') extfile(*extdesc) usage(*input);
            dcl-s total int(10) inz(0);
            read REPORT;
            dow not %eof(REPORT);
            eval total = total + ID;
            read REPORT;
            enddo;
            if total=6;
            sndpgmmsg 'SQL VIEW ACCEPTED';
            endif;
            seton LR;
            """ : """
            PGM
            DCLF FILE(QGPL/REPORT)
            RCVF
            IF COND(&ID *EQ 2 *AND &AMOUNT *EQ 100) THEN(SNDPGMMSG MSG('SQL VIEW ACCEPTED'))
            ENDPGM
            """, null);
        Ok((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/VIEWPGM) SRCFILE(QGPL/SOURCE)");
        var result = _session.Execute("CALL QGPL/VIEWPGM"); Assert.False(result.IsError, result.Message); Assert.Contains("SQL VIEW ACCEPTED", result.Message);
        Ok("RUNSQL SQL('DROP VIEW QGPL/REPORT') COMMIT(*NONE)");
    }
    [Fact]
    public void Private_views_can_read_their_jobs_private_data_and_cannot_be_persisted_in_a_permanent_schema()
    {
        Ok("RUNSQL SQL('CREATE TABLE QTEMP/PRIVATE(ID INTEGER)') COMMIT(*NONE)"); Ok("RUNSQL SQL('INSERT INTO QTEMP/PRIVATE VALUES(7)') COMMIT(*NONE)");
        Ok("RUNSQL SQL('CREATE VIEW QTEMP/REPORT AS SELECT ID FROM QTEMP/PRIVATE') COMMIT(*NONE)");
        var denied = _session.Execute("RUNSQL SQL('CREATE VIEW QGPL/REPORT AS SELECT ID FROM QTEMP/REPORT') COMMIT(*NONE)"); Assert.True(denied.IsError);
        Ok("RUNSQL SQL('DROP TABLE QTEMP/PRIVATE') COMMIT(*NONE)");
    }
}

public sealed partial class InteractiveSqlTests
{
    [Fact]
    public void Interactive_SQL_creates_replaces_reads_and_drops_views()
    {
        Seed(); Command("STRSQL"); Command("CREATE VIEW QGPL/REPORT AS SELECT ID,NAME FROM QGPL/DATA WHERE ID=1"); Assert.Contains("SQL statement completed", Screen);
        Command("SELECT NAME FROM QGPL/REPORT"); Assert.Contains("row1", Screen); Key(AidKey.Pf12);
        Command("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT ID,NAME FROM QGPL/DATA WHERE ID=2"); Assert.Contains("SQL statement completed", Screen);
        Command("SELECT NAME FROM QGPL/REPORT"); Assert.Contains("row2", Screen); Key(AidKey.Pf12);
        Command("DROP VIEW QGPL/REPORT"); Assert.Contains("SQL statement completed", Screen); Exit("2");
    }
}
