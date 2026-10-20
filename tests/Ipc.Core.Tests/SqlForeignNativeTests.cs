using Ipc.Core.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private ExecutionSession NativeForeignSession()
    { _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"ForeignNativeAdmin329"); return new(_system,_system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None); }
    [Fact]
    public void CLRPFM_rejects_a_nonempty_parent_even_when_there_are_no_dependent_rows()
    {
        Pair("ON DELETE CASCADE"); Run("DELETE FROM QGPL/CHILD");
        using var session = NativeForeignSession(); var error = session.Execute("CLRPFM FILE(QGPL/PARENT)");
        Assert.True(error.IsError); Assert.Contains("CPF3134",error.Message);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
        Assert.Equal(1L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='PARENT'"));
        Run("DELETE FROM QGPL/PARENT"); var empty = session.Execute("CLRPFM FILE(QGPL/PARENT)"); Assert.False(empty.IsError,empty.Message);
        Assert.Equal(0L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='PARENT'"));
    }
    [Fact]
    public void Native_copy_reports_CPF502D_and_restores_the_target_after_a_later_unmatched_key()
    {
        Pair(); Run("CREATE TABLE QGPL/SOURCE(ID INTEGER)"); Run("INSERT INTO QGPL/SOURCE VALUES(1),(2)");
        using var session = NativeForeignSession(); var error = session.Execute("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/CHILD) MBROPT(*REPLACE)");
        Assert.True(error.IsError); Assert.Contains("CPF502D",error.Message);
        Assert.Equal(1L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal(1L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='CHILD'"));
    }
    [Fact]
    public void Native_initialization_cannot_publish_unmatched_default_foreign_keys_or_slot_growth()
    {
        Pair(); Run("ALTER TABLE QGPL/CHILD ALTER ID SET DEFAULT 2");
        using var session = NativeForeignSession(); var error = session.Execute("INZPFM FILE(QGPL/CHILD) TOTRCDS(3)");
        Assert.True(error.IsError); Assert.Contains("CPF502D",error.Message);
        Assert.Equal(1L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(1L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='CHILD'"));
    }
    [Fact]
    public void Compiled_RPG_WRITE_and_RESTRICT_UPDATE_report_1022_and_recover_after_valid_IO()
    {
        Pair("ON UPDATE RESTRICT"); _files.CreateSourceFile("QGPL","SOURCE",sourceWidth:240);
        _files.SaveSourceMember("QGPL","SOURCE","FKRPG","""
            **free
            dcl-f CHILD extdesc('QGPL/CHILD') extfile(*extdesc) usage(*output);
            dcl-f PAR extdesc('QGPL/PARENT') extfile(*extdesc) usage(*update) prefix('P_');
            eval ID=2;
            write(e) CHILD;
            if not %error or %status(CHILD)<>1022;
            sndpgmmsg 'BAD FOREIGN WRITE';
            return;
            endif;
            eval ID=1;
            write(e) CHILD;
            if %error or %status(CHILD)<>0;
            sndpgmmsg 'BAD FOREIGN RECOVERY';
            return;
            endif;
            read PAR;
            eval P_ID=2;
            update(e) PARENT;
            if not %error or %status(PAR)<>1022;
            sndpgmmsg 'BAD RESTRICT UPDATE';
            return;
            endif;
            eval P_ID=1;
            update(e) PARENT;
            if not %error and %status(PAR)=0;
            sndpgmmsg 'FOREIGN RPG ACCEPTED';
            endif;
            seton LR;
            """,null);
        using var session = NativeForeignSession(); var compile = session.Execute("CRTBNDRPG PGM(QGPL/FKRPG) SRCFILE(QGPL/SOURCE)"); Assert.False(compile.IsError,compile.Message);
        var result = session.Execute("CALL QGPL/FKRPG"); Assert.False(result.IsError,result.Message); Assert.Contains("FOREIGN RPG ACCEPTED",result.Message);
        Assert.Equal(2L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
    }
    [Fact]
    public void Saved_scripts_queries_and_compiled_CL_share_foreign_key_creation_and_failure_monitoring()
    {
        _files.CreateSourceFile("QGPL","SOURCE",sourceWidth:240);
        _files.SaveSourceMember("QGPL","SOURCE","FKSQL","CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY);\nINSERT INTO QGPL/PARENT VALUES(1);\nCREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES QGPL/PARENT);",null);
        _files.SaveSourceMember("QGPL","SOURCE","FKQUERY","INSERT INTO QGPL/CHILD VALUES(2)",null);
        _files.SaveSourceMember("QGPL","SOURCE","FKCL","PGM\nRUNSQL SQL('INSERT INTO QGPL/CHILD VALUES(2)') COMMIT(*NONE)\nMONMSG MSGID(SQL0530) EXEC(SNDPGMMSG MSG('FOREIGN CL ACCEPTED'))\nENDPGM",null);
        using var session = NativeForeignSession();
        foreach (var command in new[] { "RUNSQLSTM QGPL/SOURCE FKSQL COMMIT(*NONE) OPTION(*NOLIST)","CRTQMQRY QGPL/FKQUERY SRCFILE(QGPL/SOURCE)","CRTCLPGM PGM(QGPL/FKCL) SRCFILE(QGPL/SOURCE)" })
        { var result = session.Execute(command); Assert.False(result.IsError,result.Message); }
        var query = session.Execute("STRQMQRY QGPL/FKQUERY"); Assert.True(query.IsError); Assert.Contains("SQL0530",query.Message);
        var call = session.Execute("CALL QGPL/FKCL"); Assert.False(call.IsError,call.Message); Assert.Contains("FOREIGN CL ACCEPTED",call.Message);
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
}
