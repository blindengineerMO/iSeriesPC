using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_standalone_arrays_supply_character_input_and_native_binary_API_receivers(int ccsid)
    {
        Compile("**free\ndcl-c COUNT 3;\ndcl-s INFO int(10) dim(32);\ndcl-s NAMES char(10) dim(COUNT);\n" +
            "dcl-s ERROR char(4) ccsid(*hex) inz(X'00000000');\neval NAMES(1)='QCCSID';\neval NAMES(2)='QSECURITY';\n" +
            "eval NAMES(3)='QPWDLVL';\ncallp QWCRSVAL(INFO:%size(INFO:*all):COUNT:NAMES:ERROR);\n" +
            "if INFO(1)=COUNT and INFO(2)=16 and INFO(9)=37 and INFO(19)=3;\nsndpgmmsg 'ARRAY API ACCEPTED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "ARRAY API ACCEPTED");
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Retained_array_elements_survive_RETURN_and_reset_after_LR_and_reclamation(int ccsid)
    {
        Compile("**free\ndcl-s COUNTS int(3) dim(2);\ndcl-s I int(10) inz(1);\neval COUNTS(I)=COUNTS(I)+1;\n" +
            "sndpgmmsg %char(COUNTS(1))+':'+%char(COUNTS(2));\nif I=2;\nseton LR;\nreturn;\nendif;\n" +
            "eval I=2;\nreturn;", group: "ARRAYRET");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "1:0"); Ok(session.Execute("CALL QGPL/APP"), "1:1"); Ok(session.Execute("CALL QGPL/APP"), "1:0");
        Ok(session.Execute("RCLACTGRP ACTGRP(ARRAYRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "1:0");
    }
    [Fact]
    public void Invalid_array_storage_prevents_program_creation()
    {
        var result = Create("**free\ndcl-s HUGE char(32767) dim(32767);\nreturn;");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
