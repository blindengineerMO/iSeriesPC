using Ipc.Cl.Commands;
using Ipc.Core.Objects;
using Ipc.Services;
using Ipc.Services.Messages;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_scalars_apply_byte_width_overflow_and_reload_with_activation_lifecycle(int ccsid)
    {
        Compile("**free\ndcl-s COUNT int(3) inz(126);\ndcl-s TEXT char(4) inz('KEEP');\ndcl-s VALUE packed(5:2);\ndcl-s DONE ind;\n" +
            "eval VALUE=-12.349;\nmonitor;\neval COUNT=COUNT+1;\non-error 103;\neval TEXT='FULL';\neval DONE=*on;\nendmon;\n" +
            "sndpgmmsg %char(COUNT)+':'+TEXT+':'+%char(VALUE);\nif DONE;\nseton LR;\nendif;\nreturn;", group: "SCALARRET");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "127:KEEP:-12.34"); Ok(session.Execute("CALL QGPL/APP"), "127:FULL:-12.34");
        Ok(session.Execute("CALL QGPL/APP"), "127:KEEP:-12.34"); Ok(session.Execute("RCLACTGRP ACTGRP(SCALARRET)"), "");
        Ok(session.Execute("CALL QGPL/APP"), "127:KEEP:-12.34");
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_scalar_CL_entry_aliases_publish_before_RETURN_and_abnormal_exit(int ccsid, bool fail)
    {
        Compile(Fixed("A", "S", "5", 'P', "0") + Fixed("B", "S", "5", 'P', "0") +
            ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("A", "PARM") + ClExternalCallTests.Calculation("B", "PARM") +
            "/free\neval A=A+1;\neval B=A+B;\n" + (fail ? "eval A=1/0;\n" : "return;\n") + "/end-free", "NATSCALAR");
        ClExternalCallTests.Create(_system, "SCALCALL", "CLP", "PGM\nDCL &N *DEC LEN(5 0) VALUE(3)\nCALL QGPL/NATSCALAR PARM(&N &N)\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)\nENDPGM");
        _files.RemoveMember("QGPL", "SOURCE", "NATSCALAR"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/SCALCALL"), "8");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_scalar_queue_calls_preserve_outer_receiver_validation_and_overlapping_aliases(int ccsid)
    {
        var store = new DataQueueStore(_system.Connections); store.Create(new("QGPL", "SCALARQ"), 8, DataQueueSequence.Keyed, 8);
        store.Send(new("QGPL", "SCALARQ"), new(Ipc.Core.Text.CodePage.ToBytes(ccsid, "DATA"), ccsid), new(Ipc.Core.Text.CodePage.ToBytes(ccsid, "SAMEKEY "), ccsid));
        Compile(Fixed("DATA", "S", "8", 'A') + Fixed("KEY", "S", "8", 'A') +
            Fixed("LEN", "S", "5", 'P', "0", "INZ(99)") + Fixed("KL", "S", "3", 'P', "0", "INZ(8)") +
            Fixed("SL", "S", "3", 'P', "0", "INZ(0)") + Fixed("ERROR", "S", "16", 'A', keywords: "CCSID(*HEX)") +
            ClExternalCallTests.Calculation("*ENTRY", "PLIST") + ClExternalCallTests.Calculation("DATA", "PARM") + ClExternalCallTests.Calculation("KEY", "PARM") +
            "/free\neval ERROR=X'00000010000000000000000000000000';\ncallp QRCVDTAQ('SCALARQ':'QGPL':LEN:DATA:0:'EQ':KL:KEY:SL:'':'*YES':8:ERROR);\n" +
            "if LEN<>99 or %subst(ERROR:9:7)<>'CPF3C3A';\neval LEN=1/0;\nendif;\nseton LR;\n/end-free", "SCALARQAPI");
        ClExternalCallTests.Create(_system, "SCALQCALL", "CLP", "PGM\nDCL &KEY *CHAR LEN(8) VALUE('SAMEKEY ')\nCALL QGPL/SCALARQAPI PARM(&KEY &KEY)\nSNDPGMMSG MSG(&KEY)\nENDPGM");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        var result = session.Execute("CALL QGPL/SCALQCALL"); Assert.False(result.IsError, result.Message); Assert.Contains("SAMEKEY", result.Message);
        Assert.Equal(1, store.Inspect(new("QGPL", "SCALARQ")).Entries);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_scalar_field_and_API_bytes_use_the_actual_job_CCSID_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s NAME char(10) inz('QCCSID');\ndcl-s INFO char(64) ccsid(*hex);\ndcl-s ERROR char(4) ccsid(*hex) inz(X'00000000');\n" +
            "callp QWCRSVAL(INFO:64:1:NAME:ERROR);\nif %subst(INFO:1:4)=X'00000001' and %subst(INFO:25:4)=X'00000025';\n" +
            "sndpgmmsg 'NATIVE SCALAR API ACCEPTED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "NATIVE SCALAR API ACCEPTED");
    }
}
