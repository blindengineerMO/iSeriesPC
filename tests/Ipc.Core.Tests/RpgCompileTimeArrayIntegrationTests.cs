using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_compile_time_arrays_load_named_inferred_numeric_and_literal_data_after_source_removal(int ccsid)
    {
        Compile("**free\ndcl-s NAMES char(7) dim(*ctdata);\ndcl-c COUNT %elem(NAMES);\ndcl-s VALUES packed(5:2) dim(COUNT) ctdata extfmt(L);\n" +
            "dcl-s TEXT char(13) dim(1) ctdata;\nif COUNT=2 and NAMES(2)='SECOND' and %xfoot(VALUES)=2.5 and TEXT(1)='/COPY MISSING';\n" +
            "sorta NAMES;\nif %lookup('FIRST':NAMES)=1;\nsndpgmmsg 'CTDATA ACCEPTED';\nendif;\nendif;\nseton LR;\n" +
            "**CTDATA NAMES\nFIRST\nSECOND\n**CTDATA TEXT\n/COPY MISSING\n**CTDATA VALUES\n+00125\n+00125\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "CTDATA ACCEPTED");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Compile_time_values_survive_RETURN_and_reload_after_LR_and_reclamation(int ccsid)
    {
        Compile("**free\ndcl-s DATA int(5) dim(2) ctdata;\nsndpgmmsg %char(DATA(1))+':'+%char(DATA(2));\n" +
            "if DATA(1)=8;\nseton LR;\nendif;\neval DATA(1)=DATA(1)+1;\nreturn;\n**CTDATA DATA\n00007\n00009\n", group: "CTRET");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "7:9"); Ok(session.Execute("CALL QGPL/APP"), "8:9"); Ok(session.Execute("CALL QGPL/APP"), "7:9");
        Ok(session.Execute("RCLACTGRP ACTGRP(CTRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "7:9");
    }
    [Theory]
    [InlineData("dcl-s DATA char(1) dim(*ctdata);\nreturn;\n**CTDATA DATA\nABC\n**CTDATA DATA\nDEF")]
    [InlineData("dcl-s DATA int(3) dim(1) ctdata extfmt(L);\nreturn;\n**CTDATA DATA\n+999")]
    [InlineData("dcl-s DATA char(2) dim(1) ctdata perrcd(2);\nreturn;\n**CTDATA DATA\nAABB")]
    public void Invalid_compile_time_data_prevents_program_creation(string source)
    {
        var result = Create("**free\n" + source); Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
