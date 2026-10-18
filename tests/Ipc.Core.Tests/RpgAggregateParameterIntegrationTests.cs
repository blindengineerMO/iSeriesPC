namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_aggregate_program_interfaces_preserve_prefixes_and_nested_aliases_after_source_removal(int ccsid)
    {
        const string template = "dcl-ds TEMPLATE qualified;\nTEXT char(3);\nN int(3);\nend-ds;\n";
        Compile("**free\n" + template + "dcl-pi *n;\nP likeds(TEMPLATE);\nA int(3) dim(2);\nend-pi;\ndcl-pr RUN extpgm('AGGSUB');\nP likeds(TEMPLATE);\nA int(3) dim(2);\nend-pr;\ncallp RUN(P:A);\nseton LR;", "AGGPI");
        Compile("**free\n" + template + "dcl-pi *n;\nP likeds(TEMPLATE);\nA int(3) dim(2);\nend-pi;\neval P.TEXT='XYZ';\neval P.N=P.N+1;\neval A(2)=A(2)+P.N;\nseton LR;", "AGGSUB");
        Compile("**free\ndcl-ds ITEM qualified;\nTEXT char(3);\nN int(3);\nend-ds;\ndcl-pr RUN extpgm('AGGPI');\nP likeds(ITEM);\nA int(3) dim(2);\nend-pr;\ndcl-s ARR int(3) dim(3) inz(7);\neval ITEM.TEXT='ABC';\neval ITEM.N=3;\ncallp RUN(ITEM:ARR);\nif ITEM.TEXT='XYZ' and ITEM.N=4 and ARR(2)=11 and ARR(3)=7;\nsndpgmmsg 'SAVED AGGREGATES OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "AGGPI", "AGGSUB" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED AGGREGATES OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_local_structure_arrays_keep_VALUE_private_and_reference_mutations_live(int ccsid)
    {
        Compile("**free\ndcl-ds ROWS qualified dim(3);\nTEXT char(3);\nN int(3);\nend-ds;\ndcl-s RESULT int(3);\neval ROWS(2).TEXT='ABC';\neval ROWS(2).N=7;\neval ROWS(3).TEXT='END';\neval RESULT=CHANGE(ROWS:ROWS);\nif ROWS(2).TEXT='XYZ' and ROWS(2).N=9 and ROWS(3).TEXT='END' and RESULT=7;\nsndpgmmsg 'SAVED VALUE ARRAYS OK';\nendif;\nseton LR;\ndcl-proc CHANGE;\ndcl-pi *n int(3);\nCOPY likeds(ROWS) dim(2) value;\nOUT likeds(ROWS) dim(2);\nend-pi;\neval OUT(2).TEXT='XYZ';\neval OUT(2).N=9;\nreturn COPY(2).N;\nend-proc;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "SAVED VALUE ARRAYS OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CL_character_storage_can_be_received_as_LIKEDS_and_forwarded_to_a_local_procedure(int ccsid)
    {
        Compile("**free\ndcl-ds TEMPLATE qualified;\nA char(2);\nB char(2);\nend-ds;\ndcl-pi *n;\nP likeds(TEMPLATE);\nend-pi;\ncallp CHANGE(P);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nOUT likeds(TEMPLATE);\nend-pi;\neval OUT.B='XY';\nreturn;\nend-proc;", "AGGPI");
        _files.SaveSourceMember("QGPL", "SOURCE", "AGGCL", "PGM\nDCL VAR(&TEXT) TYPE(*CHAR) LEN(4) VALUE('ABCD')\nCALL PGM(QGPL/AGGPI) PARM(&TEXT)\nSNDPGMMSG MSG(&TEXT)\nENDPGM", null);
        using var session = Session(ccsid); Ok(session.Execute("CRTCLPGM PGM(QGPL/AGGCL) SRCFILE(QGPL/SOURCE)"), "created");
        foreach (var member in new[] { "AGGPI", "AGGCL" }) _files.RemoveMember("QGPL", "SOURCE", member);
        Ok(session.Execute("CALL QGPL/AGGCL"), "ABXY");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Arrays_can_supply_real_QSNDDTAQ_and_QRCVDTAQ_byte_receivers(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "AGGQ"), 6);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(2) dim(3) const;\nend-pr;\ndcl-pr GET extpgm('QRCVDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0);\nDATA char(2) dim(3);\nWAIT packed(5:0) const;\nend-pr;\ndcl-s ARR char(2) dim(4) inz('ZZ');\ndcl-s LEN packed(5:0) inz(0);\neval ARR(1)='AB';\neval ARR(2)='CD';\neval ARR(3)='EF';\ncallp SEND('AGGQ':'QGPL':6:ARR);\nclear %subarr(ARR:1:3);\ncallp GET('AGGQ':'QGPL':LEN:ARR:0);\nif LEN=6 and ARR(1)='AB' and ARR(2)='CD' and ARR(3)='EF' and ARR(4)='ZZ';\nsndpgmmsg 'ARRAY API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "ARRAY API OK"); Assert.Equal(0, queues.Inspect(new("QGPL", "AGGQ")).Entries);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Typed_CL_character_constants_fill_the_entire_array_interface(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nA char(2) dim(3) const;\nend-pi;\nif A(1)='AB' and A(2)='CD' and A(3)='EF';\nsndpgmmsg 'ARRAY CONSTANT OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Ok(session.Execute("CALL PGM(QGPL/APP) PARM(('ABCDEF' (*CHAR 6)))"), "ARRAY CONSTANT OK");
    }
}
