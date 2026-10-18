using static Ipc.Core.Tests.RpgFixedDeclarationTests;
using static Ipc.Core.Tests.RpgFixedCalculationTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private static string NativeCalculationFile(bool userOpen = false)
    {
        var chars = Enumerable.Repeat(' ', 80).ToArray(); chars[5] = 'F'; "DATA".CopyTo(0, chars, 6, 4);
        chars[16] = 'I'; chars[17] = 'F'; chars[21] = 'E'; chars[33] = 'K'; "DISK".CopyTo(0, chars, 35, 4);
        if (userOpen) "USROPN".CopyTo(0, chars, 43, 6);
        return new string(chars).TrimEnd() + "\n";
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_calculations_call_QWCRSVAL_with_physical_PLIST_PARM_layouts(int ccsid)
    {
        Compile(Fixed("INFO", "S", "64", 'A', keywords: "CCSID(*HEX)") + Fixed("SIZE", "S", "10", 'I', "0", "INZ(64)") +
            Fixed("COUNT", "S", "10", 'I', "0", "INZ(1)") + Fixed("NAME", "S", "10", 'A', keywords: "INZ('QCCSID')") +
            Fixed("ERROR", "S", "4", 'A', keywords: "CCSID(*HEX) INZ(X'00000000')") +
            C("CALL", factor2: "'QWCRSVAL'", result: "P") + C("IF", expression: "%subst(INFO:1:4)=X'00000001' and") +
            C(expression: "%subst(INFO:25:4)=X'00000025'") + C("DSPLY", factor1: "'API ACCEPTED'") + C("ENDIF") + C("SETON", eq: "LR") +
            C("PLIST", factor1: "P") + C("PARM", result: "INFO") + C("PARM", result: "SIZE") + C("PARM", result: "COUNT") + C("PARM", result: "NAME") + C("PARM", result: "ERROR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "API ACCEPTED");
    }
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_calculation_entry_parameters_keep_CL_aliases_on_return_and_error(int ccsid, bool fail)
    {
        Compile(Fixed("A", "S", "5", 'P', "0") + Fixed("B", "S", "5", 'P', "0") + C("PLIST", factor1: "*ENTRY") +
            C("PARM", result: "A") + C("PARM", result: "B") + C("ADD", factor2: "1", result: "A") + C("ADD", "A", "B", "B") +
            (fail ? C("DIV", factor2: "0", result: "A") : C("RETURN")), "NATCALLEE");
        ClExternalCallTests.Create(_system, "NATCALLER", "CLP", "PGM\nDCL &N *DEC LEN(5 0) VALUE(3)\nCALL QGPL/NATCALLEE PARM(&N &N)\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)\nENDPGM");
        _files.RemoveMember("QGPL", "SOURCE", "NATCALLEE"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/NATCALLER"), "8");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_calculation_RETURN_LR_and_reclamation_preserve_activation_contracts(int ccsid)
    {
        Compile(Fixed("COUNT", "S", "3", 'I', "0", "INZ(7)") + C("DSPLY", factor1: "COUNT") + C("IF", expression: "COUNT=8") +
            C("SETON", eq: "LR") + C("ENDIF") + C("ADD", factor2: "1", result: "COUNT") + C("RETURN"), group: "NATCALCRET");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "7"); Ok(session.Execute("CALL QGPL/APP"), "8"); Ok(session.Execute("CALL QGPL/APP"), "7");
        Ok(session.Execute("RCLACTGRP ACTGRP(NATCALCRET)"), ""); Ok(session.Execute("CALL QGPL/APP"), "7");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_calculation_file_columns_set_not_found_equal_and_EOF_indicators(int ccsid)
    {
        Compile(NativeCalculationFile() + C("CHAIN", "10", "DATA", hi: "11", lo: "12") + C("IF", expression: "ID=102 and not *IN11 and not *IN12") +
            C("SETLL", "10", "DATA", hi: "11", lo: "12", eq: "13") + C("IF", expression: "*IN13 and not *IN11 and not *IN12") +
            C("SETGT", "99", "DATA", hi: "15", lo: "16") + C("IF", expression: "*IN15 and not *IN16") +
            C("SETLL", "99", "DATA", hi: "11", lo: "12", eq: "13") + C("READ", factor2: "DATA", lo: "12", eq: "14") +
            C("IF", expression: "*IN11 and not *IN13 and *IN14") + C("DSPLY", factor1: "'NATIVE IO OK'") +
            C("ENDIF") + C("ENDIF") + C("ENDIF") + C("ENDIF") + C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "NATIVE IO OK");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_calculation_file_E_extenders_report_a_closed_file_without_entering_MONITOR(int ccsid)
    {
        Compile(NativeCalculationFile(userOpen: true) + C("READ(E)", factor2: "DATA", eq: "11") + C("IF", expression: "%error() and %status(DATA)=1211") +
            C("DSPLY", factor1: "'NATIVE E OK'") + C("ENDIF") + C("SETON", eq: "LR"));
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid); Ok(session.Execute("CALL QGPL/APP"), "NATIVE E OK");
    }
}
