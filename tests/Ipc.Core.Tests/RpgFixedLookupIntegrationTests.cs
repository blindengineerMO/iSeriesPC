using static Ipc.Core.Tests.RpgFixedDeclarationTests;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Column_limited_file_and_scalar_declarations_bind_native_file_and_lookup_operations(int ccsid)
    {
        Compile("       dcl-f DATA disk keyed;\n       dcl-s ARR packed(3:0) dim(1) inz(3);\n" +
            C("SETLL", "10", "DATA") + C("IF", expression: "%equal(DATA) and %found(DATA)") + C("LOOKUP", "3", "ARR", eq: "11") +
            C("IF", expression: "%equal and *IN11") + C("DSPLY", factor1: "'FREE F OK'") + C("ENDIF") + C("ENDIF") + C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "FREE F OK");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_LOOKUP_uses_CTData_ALT_descriptions_after_source_removal(int ccsid)
    {
        Compile(Fixed("NUM", "S", "3", 'P', "1", "DIM(3) CTDATA ASCEND") + Fixed("TEXT", "S", "4", 'A', keywords: "DIM(3) ALT(NUM)") +
            Fixed("SEARCH", "S", "3", 'P', "0", "INZ(20)") + Fixed("I", "S", "5", 'I', "0", "INZ(1)") +
            C("LOOKUP", "SEARCH", "NUM(I)", hi: "11", eq: "12") + C("IF", expression: "I=2 and %equal and %found and *IN12") +
            C("DSPLY", factor1: "TEXT(I)") + C("ENDIF") + C("SETON", eq: "LR") + "**CTDATA NUM\n010ONE \n020TWO \n030THR \n");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "TWO");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_LOOKUP_retains_index_on_RETURN_and_resets_on_LR_or_reclamation(int ccsid)
    {
        Compile(Fixed("ARR", "S", "3", 'P', "0", "DIM(3) INZ(3)") + Fixed("I", "S", "5", 'P', "0", "INZ(1)") +
            C("LOOKUP", "3", "ARR(I)", eq: "11") + C("DSPLY", factor1: "I") + C("IF", expression: "I=3") + C("SETON", eq: "LR") +
            C("ENDIF") + C("ADD", factor2: "1", result: "I") + C("RETURN"), group: "LOOKRET");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        foreach (var expected in new[] { "1", "2", "3", "1" }) Ok(session.Execute("CALL QGPL/APP"), expected);
        Ok(session.Execute("RCLACTGRP ACTGRP(LOOKRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "1");
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_LOOKUP_indexes_publish_to_live_CL_entry_aliases_on_return_or_error(int ccsid, bool fail)
    {
        Compile(Fixed("ARR", "S", "3", 'P', "0", "DIM(3) INZ(3)") + Fixed("A", "S", "5", 'P', "0") + Fixed("B", "S", "5", 'P', "0") +
            C("PLIST", factor1: "*ENTRY") + C("PARM", result: "A") + C("PARM", result: "B") + C("LOOKUP", "3", "ARR(A)", eq: "11") +
            C("ADD", factor2: "1", result: "A") + C("LOOKUP", "3", "ARR(B)", eq: "11") + (fail ? C("DIV", factor2: "0", result: "A") : C("RETURN")), "LOOKCALLEE");
        ClExternalCallTests.Create(_system, "LOOKCALLER", "CLP", "PGM\nDCL &N *DEC LEN(5 0) VALUE(2)\nCALL QGPL/LOOKCALLEE PARM(&N &N)\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)\nENDPGM");
        _files.RemoveMember("QGPL", "SOURCE", "LOOKCALLEE"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/LOOKCALLER"), "3");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void LOOKUP_global_FOUND_EQUAL_remain_independent_of_file_search_status(int ccsid)
    {
        Compile(NativeCalculationFile() + Fixed("ARR", "S", "3", 'P', "0", "DIM(1) INZ(3)") +
            C("SETLL", "10", "DATA") + C("IF", expression: "%equal and %equal(DATA) and %found(DATA)") +
            C("LOOKUP", "9", "ARR", eq: "11") + C("IF", expression: "not %found and not %equal and %equal(DATA)") +
            C("SETGT", "99", "DATA") + C("IF", expression: "not %found(DATA) and %equal(DATA)") +
            C("SETLL", "99", "DATA") + C("IF", expression: "not %equal(DATA) and not %equal") +
            C("READ", factor2: "DATA") + C("CLOSE", factor2: "DATA") + C("READ(E)", factor2: "DATA") + C("LOOKUP", "3", "ARR", eq: "11") +
            C("IF", expression: "%found and %equal and %error and %eof(DATA)") + C(expression: "and %eof and %status=1211 and") +
            C(expression: "%status(DATA)=1211") + C(expression: "and not %found(DATA) and not %equal(DATA)") + C("DSPLY", factor1: "'STATUS OK'") +
            C("ENDIF") + C("ENDIF") + C("ENDIF") + C("ENDIF") + C("ENDIF") + C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "STATUS OK");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_column_limited_DS_declarations_support_native_LOOKUP_and_free_LOOKUPNE(int ccsid)
    {
        Compile("       dcl-c COUNT 3;\n       dcl-ds DS qualified;\n       ARR int(5) dim(COUNT) inz(2);\n       I int(5) inz(2);\n       end-ds;\n" +
            C("LOOKUP", "2", "DS.ARR(DS.I)", eq: "11") + C("IF", expression: "DS.I=2 and *IN11 and") +
            C(expression: "%lookupne(2:DS.ARR)=0") + C("DSPLY", factor1: "'DS LOOKUP OK'") + C("ENDIF") + C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "DS LOOKUP OK");
    }
}
