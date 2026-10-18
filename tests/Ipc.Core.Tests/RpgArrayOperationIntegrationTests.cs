namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_array_operations_sort_search_total_and_update_slices_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s NUM int(5) dim(5) ascend;\neval NUM(1)=9;\neval NUM(2)=3;\neval NUM(3)=7;\n" +
            "eval NUM(4)=1;\neval NUM(5)=5;\nsorta NUM;\nif %lookup(7:NUM)=4 and %lookuple(6:NUM)=3 and %xfoot(NUM)=25;\n" +
            "eval %subarr(NUM:2:2)=2;\nif %xfoot(%subarr(NUM:2:2))=4 and NUM(1)=1 and NUM(5)=9;\n" +
            "sndpgmmsg 'ARRAY OPERATIONS ACCEPTED';\nendif;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "ARRAY OPERATIONS ACCEPTED");
    }
    [Theory]
    [InlineData(37, "aA0")]
    [InlineData(1208, "0Aa")]
    public void Character_sort_uses_actual_job_CCSID_in_saved_programs(int ccsid, string expected)
    {
        Compile("**free\ndcl-s TEXT char(1) dim(3) ascend;\neval TEXT(1)='A';\neval TEXT(2)='0';\neval TEXT(3)='a';\n" +
            "sorta TEXT;\nsndpgmmsg TEXT(1)+TEXT(2)+TEXT(3);\nseton LR;");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid); Ok(session.Execute("CALL QGPL/APP"), expected);
    }
}
