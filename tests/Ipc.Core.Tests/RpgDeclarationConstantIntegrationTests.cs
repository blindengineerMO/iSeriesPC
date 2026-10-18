using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_constants_supply_native_API_parameters_and_keep_compiled_layout_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-c INFO_LENGTH 128;\ndcl-c ERROR_LENGTH 16;\ndcl-c COUNT 1;\ndcl-c NAMES 'QCCSID    ';\n" +
            "dcl-ds INFO qualified inz len(INFO_LENGTH);\nCOUNT int(10);\nOFFSET int(10);\nCCSID int(10) pos(25);\nend-ds;\n" +
            "dcl-ds ERROR qualified inz len(ERROR_LENGTH);\nPROVIDED int(10) inz(ERROR_LENGTH);\nAVAILABLE int(10);\nend-ds;\n" +
            "dcl-c ACTUAL_LENGTH %size(INFO);\ncallp QWCRSVAL(INFO:ACTUAL_LENGTH:COUNT:NAMES:ERROR);\n" +
            "if INFO.COUNT=COUNT and INFO.OFFSET=8 and INFO.CCSID=37 and ERROR.AVAILABLE=0;\nsndpgmmsg 'CONSTANT API ACCEPTED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "CONSTANT API ACCEPTED");
    }
    [Fact]
    public void Runtime_dependent_constant_rejection_does_not_create_a_program_or_run_code()
    {
        var result = Create("**free\ndcl-s N int(10) inz(2);\ndcl-c COUNT N;\ncallp QCMDEXC('CRTLIB BADCONST':15);\nreturn;");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
        Assert.False(_system.Objects.Exists("QSYS", "BADCONST", ObjectType.Library));
    }
}
