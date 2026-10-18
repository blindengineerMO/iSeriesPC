using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, "312:321", 2)]
    [InlineData(1208, "321:312", 3)]
    public void Saved_keyed_sort_FIELDS_lookup_and_slices_use_actual_job_CCSID_after_source_removal(int ccsid, string expected, int found)
    {
        Compile("**free\ndcl-ds ROWS qualified dim(3) inz;\nID char(1);\nCODE char(1);\nPAY int(5);\nend-ds;\ndcl-s ORDER char(3);\n" +
            "eval ROWS(1).ID='1';\neval ROWS(1).CODE='a';\neval ROWS(1).PAY=2;\n" +
            "eval ROWS(2).ID='2';\neval ROWS(2).CODE='A';\neval ROWS(2).PAY=2;\n" +
            "eval ROWS(3).ID='3';\neval ROWS(3).CODE='0';\neval ROWS(3).PAY=1;\nsorta ROWS %fields(PAY:CODE);\n" +
            "eval ORDER=ROWS(1).ID+ROWS(2).ID+ROWS(3).ID;\nif %lookup('a':ROWS(*).CODE)=" + found + ";\n" +
            "sorta(d) %subarr(ROWS(*).CODE:2:2);\nsndpgmmsg ORDER+':'+ROWS(1).ID+ROWS(2).ID+ROWS(3).ID;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), expected);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Retained_keyed_array_records_survive_RETURN_and_reset_after_LR_and_reclamation(int ccsid)
    {
        Compile("**free\ndcl-ds ROWS qualified dim(2) inz;\nID char(1);\nKEY int(5);\nend-ds;\ndcl-s CALLS int(5);\n" +
            "eval CALLS=CALLS+1;\nif CALLS=1;\neval ROWS(1).ID='A';\neval ROWS(1).KEY=2;\neval ROWS(2).ID='B';\neval ROWS(2).KEY=1;\n" +
            "sorta ROWS %fields(KEY);\nendif;\nsndpgmmsg %char(CALLS)+':'+ROWS(1).ID+ROWS(2).ID;\nif CALLS=2;\nseton LR;\nendif;\nreturn;", group: "KEYRET");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "1:BA"); Ok(session.Execute("CALL QGPL/APP"), "2:BA"); Ok(session.Execute("CALL QGPL/APP"), "1:BA");
        Ok(session.Execute("RCLACTGRP ACTGRP(KEYRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "1:BA");
    }
    [Theory]
    [InlineData("ROWS %fields(MISSING)")]
    [InlineData("ROWS %fields(ROWS.KEY)")]
    [InlineData("ROWS(*).MISSING")]
    public void Invalid_keyed_sort_prevents_program_creation(string operand)
    {
        var result = Create("**free\ndcl-ds ROWS qualified dim(3);\nKEY int(5);\nend-ds;\nsorta " + operand + ";\nreturn;");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
