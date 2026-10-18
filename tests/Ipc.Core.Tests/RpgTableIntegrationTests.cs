using static Ipc.Core.Tests.RpgFixedDeclarationTests;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_tables_load_alternating_CTData_and_select_both_current_elements(int ccsid)
    {
        Compile(Fixed("TABNUM", "S", "3", 'P', "1", "DIM(3) CTDATA PERRCD(2)") + Fixed("TABTXT", "S", "2", 'A', keywords: "DIM(3) ALT(TABNUM)") +
            C("LOOKUP", "20", "TABNUM", "TABTXT", eq: "11") + C("IF", expression: "TABNUM=2 and TABTXT='BB' and *IN11") +
            C("DSPLY", factor1: "'TABLE PAIR'") + C("ENDIF") + C("SETON", eq: "LR") + "**CTDATA TABNUM\n010AA020BB\n030CC\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "TABLE PAIR");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Table_current_selection_is_retained_on_RETURN_and_reinitialized_on_LR_and_reclamation(int ccsid)
    {
        Compile(Fixed("TABCHAR", "S", "1", 'A', keywords: "DIM(2) CTDATA") + C("DSPLY", factor1: "TABCHAR") +
            C("IF", expression: "TABCHAR='B'") + C("SETON", eq: "LR") + C("ENDIF") + C("EVAL", expression: "*IN11=%tlookup('B':TABCHAR)") +
            C("RETURN") + "**CTDATA TABCHAR\nA\nB\n", group: "TBLRET");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "A"); Ok(session.Execute("CALL QGPL/APP"), "B"); Ok(session.Execute("CALL QGPL/APP"), "A");
        Ok(session.Execute("RCLACTGRP ACTGRP(TBLRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "A");
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Current_table_elements_pass_live_aliases_to_CL_and_keep_writes_on_return_or_error(int ccsid, bool fail)
    {
        var body = "PGM PARM(&A &B)\nDCL &A *DEC LEN(5 0)\nDCL &B *DEC LEN(5 0)\nCHGVAR &A (&A + 1)\nCHGVAR &B (&A + &B)\n" +
            (fail ? "SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('Table caller failure') MSGTYPE(*ESCAPE)\n" : "") + "ENDPGM";
        ClExternalCallTests.Create(_system, "TABLEUPD", "CLP", body);
        Compile(Fixed("TABNUM", "S", "5", 'P', "0", "DIM(2) CTDATA") + C("LOOKUP", "7", "TABNUM", eq: "11") +
            C("CALLP(E)", expression: "TABLEUPD(TABNUM:TABNUM)") + C("DSPLY", factor1: "TABNUM") + C("SETON", eq: "LR") + "**CTDATA TABNUM\n00003\n00007\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "16");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Current_table_elements_are_exact_width_native_API_receivers(int ccsid)
    {
        Compile("**free\ndcl-s TABINFO char(64) dim(2) ccsid(*hex);\ndcl-s SIZE int(10) inz(64);\ndcl-s COUNT int(10) inz(1);\n" +
            "dcl-s NAME char(10) inz('QCCSID');\ndcl-s ERROR char(4) ccsid(*hex) inz(X'00000000');\n" +
            "callp QWCRSVAL(TABINFO:SIZE:COUNT:NAME:ERROR);\n" +
            "if %subst(TABINFO:1:4)=X'00000001' and %subst(TABINFO:25:4)=X'00000025';\n" +
            "if %tlookup('':TABINFO) and %tlookupne('':TABINFO);\n" +
            "if %subst(TABINFO:1:4)=X'00000001';\nsndpgmmsg 'TABLE API OK';\nendif;\nendif;\nendif;\nseton LR;\n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "TABLE API OK");
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Stored_RPG_calls_keep_live_one_byte_integer_aliases_on_return_and_error(int ccsid, bool fail)
    {
        Compile(Fixed("A", "S", "3", 'I', "0") + Fixed("B", "S", "3", 'I', "0") + C("PLIST", factor1: "*ENTRY") + C("PARM", result: "A") + C("PARM", result: "B") +
            C("ADD", factor2: "1", result: "A") + C("ADD", "A", "B", "B") + (fail ? C("DIV", factor2: "0", result: "A") : C("RETURN")), "TBLRPG");
        Compile(Fixed("N", "S", "3", 'I', "0", "INZ(7)") + C("CALLP(E)", expression: "TBLRPG(N:N)") + C("DSPLY", factor1: "N") + C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "TBLRPG"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "16");
    }
}
