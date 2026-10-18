namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_procedures_keep_VALUE_reference_return_and_static_semantics_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s N int(3) inz(7);\ndcl-s RESULT int(20);\ndcl-s ONE int(10);\ndcl-s TWO int(10);\neval RESULT=FACT(N);\neval ONE=NEXT();\neval TWO=NEXT();\nTOUCH(N);\n" +
            "if RESULT=5040 and N=8 and ONE=1 and TWO=2;\nsndpgmmsg 'PROCEDURE VALUES OK';\nendif;\nseton LR;\n" +
            "dcl-proc FACT;\ndcl-pi *n int(20);\nN int(3) value;\nend-pi;\nif N<=1;\nreturn 1;\nendif;\nreturn N*FACT(N-1);\nend-proc;\n" +
            "dcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\ndcl-s N int(10) static inz(0);\neval N=N+1;\nreturn N;\nend-proc;\n" +
            "dcl-proc TOUCH;\ndcl-pi *n;\nN int(3);\nend-pi;\neval N=N+1;\nreturn;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "PROCEDURE VALUES OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Procedure_static_storage_retains_with_the_module_and_resets_after_activation_group_reclaim(int ccsid)
    {
        Compile("**free\ndcl-s N int(10);\neval N=NEXT();\nsndpgmmsg %char(N);\nreturn;\ndcl-proc NEXT;\ndcl-pi *n int(10) end-pi;\ndcl-s N int(10) static inz(0);\neval N=N+1;\nreturn N;\nend-proc;", "APP", "PROCS");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Assert.EndsWith("1", session.Execute("CALL QGPL/APP").Message?.Trim()); Assert.EndsWith("2", session.Execute("CALL QGPL/APP").Message?.Trim());
        Ok(session.Execute("RCLACTGRP ACTGRP(PROCS)"), ""); Assert.EndsWith("1", session.Execute("CALL QGPL/APP").Message?.Trim());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_functions_can_call_real_system_APIs_with_private_local_receivers_and_CONST_inputs(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "PROCQ"), 8);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(8) const;\nend-pr;\n" +
            "dcl-pr GET extpgm('QRCVDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0);\nDATA char(8);\nWAIT packed(5:0) const;\nend-pr;\n" +
            "dcl-s OUT char(8);\neval OUT=ROUNDTRIP('HELLO');\nif OUT='HELLO';\nsndpgmmsg 'PROCEDURE API OK';\nendif;\nseton LR;\n" +
            "dcl-proc ROUNDTRIP;\ndcl-pi *n char(8);\nINPUT char(8) const;\nend-pi;\ndcl-s LEN packed(5:0) inz(0);\ndcl-s DATA char(8);\n" +
            "callp SEND('PROCQ':'QGPL':5:INPUT);\ncallp GET('PROCQ':'QGPL':LEN:DATA:0);\nif LEN=5;\nreturn DATA;\nendif;\nreturn 'FAILED';\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "PROCEDURE API OK"); Assert.Equal(0, queues.Inspect(new("QGPL", "PROCQ")).Entries);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Functions_share_global_disk_positions_and_record_fields_without_closing_the_file_at_return(int ccsid)
    {
        Compile("**free\ndcl-f DATA disk usage(*input);\ndcl-s A int(10);\ndcl-s B int(10);\neval A=NEXTROW();\neval B=NEXTROW();\n" +
            "if A=101 and B=102;\nsndpgmmsg 'PROCEDURE FILE OK';\nendif;\nseton LR;\ndcl-proc NEXTROW;\ndcl-pi *n int(10) end-pi;\nread DATA;\nreturn ID;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "PROCEDURE FILE OK");
    }

    [Fact]
    public void Native_procedures_cannot_use_module_files_with_INFSR()
    {
        var result = Create("**free\ndcl-f DATA disk usage(*input) infsr(FAILED);\nreturn;\nbegsr FAILED;\nreturn;\nendsr;\ndcl-proc GET;\nread DATA;\nreturn;\nend-proc;");
        Assert.True(result.IsError); Assert.Contains("INFSR", result.Message);
    }
}
