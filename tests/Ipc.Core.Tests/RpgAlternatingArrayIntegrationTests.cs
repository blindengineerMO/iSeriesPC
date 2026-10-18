using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, "aA0:6")]
    [InlineData(1208, "0Aa:6")]
    public void Saved_alternating_arrays_resolve_forward_inferred_definitions_and_keep_sort_storage_independent(int ccsid, string expected)
    {
        Compile("**free\ndcl-s OTHER int(3) dim(*ctdata) alt(MAIN);\ndcl-s MAIN char(1) dim(*ctdata);\ndcl-c COUNT %elem(OTHER);\n" +
            "sorta MAIN;\nif COUNT=3 and OTHER(1)=1 and OTHER(2)=2 and OTHER(3)=3;\n" +
            "sndpgmmsg MAIN(1)+MAIN(2)+MAIN(3)+':'+%char(%xfoot(OTHER));\nendif;\nseton LR;\n**CTDATA MAIN\na001\nA002\n0003\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), expected);
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Alternating_array_mutations_survive_RETURN_and_reload_after_LR_and_reclamation(int ccsid)
    {
        Compile("**free\ndcl-s MAIN char(2) dim(2) ctdata perrcd(2);\ndcl-s OTHER int(3) dim(2) alt(MAIN);\n" +
            "sndpgmmsg %char(OTHER(1))+':'+MAIN(1);\nif OTHER(1)=8;\nseton LR;\nendif;\neval OTHER(1)=OTHER(1)+1;\nreturn;\n**CTDATA MAIN\nAA007BB009\n", group: "ALTRET");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "7:AA"); Ok(session.Execute("CALL QGPL/APP"), "8:AA"); Ok(session.Execute("CALL QGPL/APP"), "7:AA");
        Ok(session.Execute("RCLACTGRP ACTGRP(ALTRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "7:AA");
    }
    [Theory]
    [InlineData("dcl-s MAIN char(1) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) alt(MAIN) perrcd(1);")]
    [InlineData("dcl-s MAIN char(1) dim(2) ctdata;\ndcl-s OTHER int(3) dim(2) alt(MAIN);\ndcl-s THIRD int(3) dim(2) alt(MAIN);")]
    [InlineData("dcl-s MAIN char(51) dim(1) ctdata;\ndcl-s OTHER char(50) dim(1) alt(MAIN);")]
    public void Invalid_alternating_array_contracts_prevent_program_creation(string source)
    {
        var result = Create("**free\n" + source + "\nreturn;"); Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
