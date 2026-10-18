using Ipc.Core.Objects;
using static Ipc.Core.Tests.RpgFixedDeclarationTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, 0, "aA0:6")] [InlineData(1208, 0, "0Aa:6")]
    [InlineData(37, 1, "aA0:6")] [InlineData(1208, 1, "0Aa:6")]
    public void Saved_traditional_arrays_compile_alternating_data_and_sort_using_the_actual_job_CCSID(int ccsid, int shift, string expected)
    {
        Compile(Fixed("OTHER", "S", "3", 'I', "0", "DIM(*CTDATA) ALT(MAIN)", shift) + Fixed("MAIN", "S", "1", keywords: "DIM(*CTDATA)", shift: shift) +
            Fixed("COUNT", "C", keywords: "%ELEM(OTHER)", shift: shift) + "/free\nsorta MAIN;\n" +
            "if COUNT=3 and OTHER(1)=1 and OTHER(2)=2 and OTHER(3)=3;\nsndpgmmsg MAIN(1)+MAIN(2)+MAIN(3)+':'+%char(%xfoot(OTHER));\nendif;\nseton LR;\n/end-free\n**CTDATA MAIN\na001\nA002\n0003\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), expected);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_traditional_arrays_expose_native_binary_receivers_and_character_API_input(int ccsid)
    {
        Compile(Fixed("COUNT", "C", keywords: "CONST(3)") + Fixed("INFO", "S", "10", 'I', "0", "DIM(32)") + Fixed("NAMES", "S", "10", 'A', keywords: "DIM(COUNT)") +
            Fixed("ERROR", "S", "4", 'A', keywords: "CCSID(*HEX) INZ(X'00000000')") +
            "/free\neval NAMES(1)='QCCSID';\neval NAMES(2)='QSECURITY';\neval NAMES(3)='QPWDLVL';\n" +
            "callp QWCRSVAL(INFO:%size(INFO:*all):COUNT:NAMES:ERROR);\nif INFO(1)=COUNT and INFO(2)=16 and INFO(9)=37 and INFO(19)=3;\n" +
            "sndpgmmsg 'FIXED ARRAY API ACCEPTED';\nendif;\nseton LR;\n/end-free");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "FIXED ARRAY API ACCEPTED");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Traditional_array_mutations_survive_RETURN_and_reload_after_LR_and_reclaim(int ccsid)
    {
        Compile(Fixed("VALUES", "S", "3", 'I', "0", "DIM(2)") + Fixed(keywords: "CTDATA PERRCD(2)") +
            "/free\nsndpgmmsg %char(VALUES(1))+':'+%char(VALUES(2));\nif VALUES(1)=8;\nseton LR;\nendif;\n" +
            "eval VALUES(1)=VALUES(1)+1;\nreturn;\n/end-free\n**CTDATA VALUES\n007009\n", group: "FIXEDRET");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "7:9"); Ok(session.Execute("CALL QGPL/APP"), "8:9"); Ok(session.Execute("CALL QGPL/APP"), "7:9");
        Ok(session.Execute("RCLACTGRP ACTGRP(FIXEDRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "7:9");
    }
    [Theory]
    [InlineData("DIM(0)")]
    [InlineData("DIM(2) CTDATA INZ(0)")]
    [InlineData("DIM(2) FROMFILE(DATA)")]
    public void Invalid_traditional_array_definitions_prevent_saved_program_creation(string keywords)
    {
        var result = Create(Fixed("BAD", "S", "3", 'I', "0", keywords) + "/free\nreturn;\n/end-free");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
