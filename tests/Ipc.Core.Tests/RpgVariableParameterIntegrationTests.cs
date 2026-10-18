namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_variable_interfaces_preserve_actual_lengths_aliases_and_retained_rebinding(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nP char(100) options(*varsize);\nLEN int(3);\nend-pi;\neval %subst(P:LEN:1)='X';\nreturn;", "VARPI", "VARARGS");
        Compile("**free\ndcl-pr RUN extpgm('VARPI');\nP char(100) options(*varsize);\nLEN int(3);\nend-pr;\ndcl-s A char(4) inz('ABCD');\ndcl-s B char(6) inz('ABCDEF');\ndcl-s N int(3) inz(4);\ncallp RUN(A:N);\neval N=6;\ncallp RUN(B:N);\nif A='ABCX' and B='ABCDEX';\nsndpgmmsg 'SAVED VARSIZE OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "VARPI" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED VARSIZE OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Variable_native_queue_receivers_use_actual_buffers_after_source_removal(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "VARQ"), 6);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(32767) const options(*varsize);\nend-pr;\ndcl-pr GET extpgm('QRCVDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0);\nDATA char(32767) options(*varsize);\nWAIT packed(5:0) const;\nend-pr;\ndcl-s TEXT char(6) inz('ABCDEF');\ndcl-s LEN packed(5:0) inz(0);\ncallp SEND('VARQ':'QGPL':6:TEXT);\neval TEXT='';\ncallp GET('VARQ':'QGPL':LEN:TEXT:0);\nif TEXT='ABCDEF' and LEN=6;\nsndpgmmsg 'VARIABLE API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "VARIABLE API OK"); Assert.Equal(0, queues.Inspect(new("QGPL", "VARQ")).Entries);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CL_variables_and_explicit_constants_keep_their_actual_size_at_variable_RPG_entries(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nP char(100) options(*varsize);\nend-pi;\neval %subst(P:2:2)='XY';\nsndpgmmsg %subst(P:1:4);\nreturn;", "VARPI");
        _files.SaveSourceMember("QGPL", "SOURCE", "VARCL", "PGM\nDCL VAR(&TEXT) TYPE(*CHAR) LEN(4) VALUE('ABCD')\nCALL PGM(QGPL/VARPI) PARM(&TEXT)\nSNDPGMMSG MSG(&TEXT)\nCALL PGM(QGPL/VARPI) PARM(('ABCD' (*CHAR 4)))\nENDPGM", null);
        using var session = Session(ccsid); Ok(session.Execute("CRTCLPGM PGM(QGPL/VARCL) SRCFILE(QGPL/SOURCE)"), "created");
        foreach (var member in new[] { "VARPI", "VARCL" }) _files.RemoveMember("QGPL", "SOURCE", member);
        Ok(session.Execute("CALL QGPL/VARCL"), "AXYD");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Unprototyped_APIs_cannot_write_through_a_variable_CONST_interface(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "VARQ"), 4);
        queues.Send(new("QGPL", "VARQ"), new Ipc.Core.Work.ProgramBuffer(Ipc.Core.Text.CodePage.ToBytes(ccsid, "DATA"), ccsid));
        Compile("**free\ndcl-s TEXT char(4) inz('ABCD');\ncallp TEST(TEXT);\nseton LR;\ndcl-proc TEST;\ndcl-pi *n;\nP char(100) const options(*varsize);\nend-pi;\ndcl-s LEN packed(5:0) inz(99);\ncallp(e) QRCVDTAQ('VARQ':'QGPL':LEN:P:0);\nif %error() and LEN=99 and %subst(P:1:4)='ABCD';\nsndpgmmsg 'VARIABLE CONST PROTECTED';\nendif;\nreturn;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "VARIABLE CONST PROTECTED"); Assert.Equal(1, queues.Inspect(new("QGPL", "VARQ")).Entries);
    }
}
