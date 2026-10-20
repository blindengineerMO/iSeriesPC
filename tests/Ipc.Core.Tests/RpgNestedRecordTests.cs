using Ipc.Console.Session;
using Ipc.Session;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Nested_WORKSTN_record_buffers_exchange_saved_global_and_local_files(int ccsid, bool local)
    {
        Display(Screen);
        var definition = "dcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc);\n";
        var body = "dcl-ds BOX qualified dim(2) inz;\nHEAD char(2);\nROW likerec(ENTRY:*all) dim(2);\nTAIL char(2);\nend-ds;\neval BOX(2).HEAD='AA';\neval BOX(2).TAIL='ZZ';\neval BOX(2).ROW(1).NAME='KEEP';\neval BOX(2).ROW(2).NAME='NESTED';\neval BOX(2).ROW(2).AMOUNT=1.25;\nexfmt ENTRY BOX(2).ROW(2);\nif BOX(2).ROW(2).NAME='DONE' and BOX(2).ROW(2).AMOUNT=2.5 and BOX(2).ROW(1).NAME='KEEP' and BOX(2).HEAD='AA' and BOX(2).TAIL='ZZ';\nsndpgmmsg 'NESTED DISPLAY OK';\nendif;\n";
        Compile("**free\n" + (local ? "callp SHOW();\nseton LR;\ndcl-proc SHOW;\n" + definition + "dcl-pi *n end-pi;\n" + body + "end-proc;" : definition + body + "seton LR;"));
        _sources.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, new Terminal(d => {
            Assert.Contains("NESTED", d.Session.Buffer.RowText(4)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd));
            foreach (var ch in "DONE") d.Session.Handle(new(AidKey.None, Character: ch)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.Tab)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd));
            foreach (var ch in "2.5") d.Session.Handle(new(AidKey.None, Character: ch)); return d.Session.Handle(new(AidKey.Enter)); }));
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("NESTED DISPLAY OK", result.Message);
    }
}

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_DISK_record_buffers_preserve_siblings_and_support_saved_read_update_write(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed usage(*update:*output);\ndcl-ds BOX qualified inz;\nHEAD char(2);\nROWS likerec(ROW:*all) dim(2);\nTAIL char(2);\nend-ds;\neval BOX.HEAD='AA';\neval BOX.TAIL='ZZ';\neval BOX.ROWS(1).ID=7;\nchain 20 DATA BOX.ROWS(2);\neval BOX.ROWS(2).TEXT='changed';\nupdate ROW BOX.ROWS(2);\neval BOX.ROWS(2).ID=999;\neval BOX.ROWS(2).KEY=99;\nwrite ROW BOX.ROWS(2);\nchain 99 DATA BOX.ROWS(2);\nif BOX.ROWS(2).ID=999 and BOX.ROWS(2).TEXT='changed' and BOX.ROWS(1).ID=7 and BOX.HEAD='AA' and BOX.TAIL='ZZ';\nsndpgmmsg 'NESTED DISK OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NESTED DISK OK");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_KDS_array_elements_capture_the_index_once_for_saved_keyed_reads(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed;\ndcl-ds BOX qualified inz;\nKEYS likerec(ROW:*key) dim(2);\nend-ds;\ndcl-ds BUFFER likerec(ROW);\ndcl-s COUNT int(10);\neval BOX.KEYS(2).KEY=20;\nchain %kds(BOX.KEYS(NEXT())) DATA BUFFER;\nif COUNT=1 and BUFFER.ID=101;\nsndpgmmsg 'NESTED KEYS OK';\nendif;\nseton LR;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\neval COUNT=COUNT+1;\nreturn 2;\nend-proc;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NESTED KEYS OK");
    }

}
