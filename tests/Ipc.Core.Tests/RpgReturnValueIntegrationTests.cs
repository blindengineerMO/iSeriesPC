namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_array_returns_preserve_defaults_receiver_tails_and_RTNParm_counts_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s A int(3) dim(4) inz(9);\ndcl-s COUNT int(3);\neval A=BUILD(7);\neval COUNT=GETCOUNT();\nif A(1)=7 and A(2)=7 and A(3)=0 and A(4)=9 and COUNT=1;\nsndpgmmsg 'SAVED ARRAY RETURNS OK';\nendif;\nseton LR;\ndcl-proc BUILD;\ndcl-pi *n int(3) dim(3) rtnparm;\nN int(3) value;\nend-pi;\ndcl-s LOCAL int(3) dim(2);\neval LOCAL=N;\nreturn LOCAL;\nend-proc;\ndcl-proc GETCOUNT;\ndcl-pi *n int(3) rtnparm end-pi;\nreturn %parms();\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED ARRAY RETURNS OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_structure_returns_copy_qualified_data_and_can_feed_CONST_parameters(int ccsid)
    {
        Compile("**free\ndcl-ds ITEM qualified;\nN int(3);\nTEXT char(3);\nend-ds;\ndcl-ds COPY likeds(ITEM);\ndcl-s RESULT int(3);\neval ITEM.N=7;\neval ITEM.TEXT='ABC';\neval COPY=BUILD();\neval RESULT=USE(BUILD());\neval ITEM.N=9;\nif COPY.N=7 and COPY.TEXT='ABC' and RESULT=7;\nsndpgmmsg 'SAVED STRUCTURE RETURNS OK';\nendif;\nseton LR;\ndcl-proc BUILD;\ndcl-pi *n likeds(ITEM) rtnparm end-pi;\nreturn ITEM;\nend-proc;\ndcl-proc USE;\ndcl-pi *n int(3);\nP likeds(ITEM) const;\nend-pi;\nreturn P.N;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED STRUCTURE RETURNS OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Returned_arrays_can_supply_CONST_byte_inputs_to_real_native_queue_APIs(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "RETURNQ"), 6);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(2) dim(3) const;\nend-pr;\ncallp SEND('RETURNQ':'QGPL':6:BUILD());\nsndpgmmsg 'RETURN QUEUE OK';\nseton LR;\ndcl-proc BUILD;\ndcl-pi *n char(2) dim(3) rtnparm end-pi;\ndcl-s LOCAL char(2) dim(3);\neval LOCAL(1)='AB';\neval LOCAL(2)='CD';\neval LOCAL(3)='EF';\nreturn LOCAL;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "RETURN QUEUE OK");
        Assert.Equal("ABCDEF", queues.Receive(new("QGPL", "RETURNQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_structure_array_returns_preserve_images_across_independent_invocations(int ccsid)
    {
        Compile("**free\ndcl-ds ROWS qualified dim(3);\nTEXT char(3);\nend-ds;\ndcl-ds OTHER likeds(ROWS) dim(3);\neval ROWS=BUILD('ABC');\neval OTHER=BUILD('XYZ');\nif ROWS(1).TEXT='ABC' and ROWS(3).TEXT='ABC' and OTHER(2).TEXT='XYZ';\nsndpgmmsg 'SAVED STRUCTURE ARRAY RETURNS OK';\nendif;\nseton LR;\ndcl-proc BUILD;\ndcl-pi *n likeds(ROWS) dim(3) rtnparm;\nTEXT char(3) const;\nend-pi;\nreturn TEXT;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED STRUCTURE ARRAY RETURNS OK");
    }
}
