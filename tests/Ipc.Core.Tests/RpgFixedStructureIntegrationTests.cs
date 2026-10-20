using Ipc.Core.Objects;
using Ipc.Console.Session;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, 0)] [InlineData(1208, 0)] [InlineData(37, 1)] [InlineData(1208, 1)]
    public void Saved_traditional_structures_receive_live_native_API_bytes_after_source_removal(int ccsid, int shift)
    {
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        Compile(D("INFO", "DS", length: "128", keywords: "QUALIFIED", shift: shift) +
            D("COUNT", from: "1", length: "4", type: 'I', decimals: "0", shift: shift) +
            D("OFFSET", from: "5", length: "8", type: 'I', decimals: "0", shift: shift) +
            D("CCSID", from: "33", length: "36", type: 'I', decimals: "0", shift: shift) +
            D("SECURITY", from: "53", length: "54", shift: shift) +
            D("PASSWORD", from: "73", length: "76", type: 'I', decimals: "0", shift: shift) +
            D("ERROR", "DS", length: "16", keywords: "QUALIFIED INZ", shift: shift) +
            D("PROVIDED", length: "10", type: 'I', decimals: "0", keywords: "INZ(16)", shift: shift) +
            D("AVAILABLE", length: "10", type: 'I', decimals: "0", shift: shift) +
            D("ID", length: "7", shift: shift) +
            "/free\ncallp QWCRSVAL(INFO:%size(INFO):3:'QCCSID    QSECURITY QPWDLVL   ':ERROR);\n" +
            "if INFO.COUNT=3 and INFO.OFFSET=16 and INFO.CCSID=37 and INFO.SECURITY='40' and INFO.PASSWORD=3 and ERROR.AVAILABLE=0;\n" +
            "sndpgmmsg 'FIXED DS API OK';\nendif;\nseton LR;\n/end-free");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "FIXED DS API OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_fixed_INFDS_and_PSDS_share_real_file_and_program_status(int ccsid)
    {
        Compile("       dcl-f DATA keyed infds(F);\n" +
            D("F", "DS", keywords: "QUALIFIED") + D("NAME", from: "*FILE") + D("CODE", from: "*STATUS") + D("OP", from: "*OPCODE") +
            D("PS", "DS", keywords: "QUALIFIED", status: 'S') + D("CODE", from: "*STATUS") + D("PROC", from: "*PROC") +
            "/free\nchain 999 ROW;\nmonitor;\neval VALUE=1/0;\non-error 102;\nendmon;\n" +
            "if F.CODE=12 and F.NAME='DATA' and F.OP='CHAINR' and PS.CODE=102 and PS.PROC='APP';\nsndpgmmsg 'FIXED FEEDBACK OK';\nendif;\nseton LR;\n/end-free");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "FIXED FEEDBACK OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_occurrences_retain_selection_after_RETURN_and_reload_after_LR_or_reclaim(int ccsid)
    {
        Compile(D("BANK", "DS", keywords: "QUALIFIED OCCURS(2) INZ") + D("N", length: "3", type: 'P', decimals: "0", keywords: "INZ(7)") +
            "/free\nsndpgmmsg %char(%occur(BANK))+':'+%char(BANK.N);\nif %occur(BANK)=2;\nseton LR;\nendif;\neval %occur(BANK)=2;\neval BANK.N=9;\nreturn;\n/end-free", group: "FIXEDDS");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "1:7"); Ok(session.Execute("CALL QGPL/APP"), "2:9"); Ok(session.Execute("CALL QGPL/APP"), "1:7");
        Ok(session.Execute("RCLACTGRP ACTGRP(FIXEDDS)"), ""); Ok(session.Execute("CALL QGPL/APP"), "1:7");
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_fixed_subfield_calls_preserve_live_CL_aliases_on_return_and_error(int ccsid, bool fail)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "UPDATE", "PGM PARM(&A &B)\nDCL &A *DEC LEN(5 0)\nDCL &B *DEC LEN(5 0)\nCHGVAR &A (&A + 1)\nCHGVAR &B (&A + &B)\n" +
            (fail ? "SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('EXPECTED') MSGTYPE(*ESCAPE)\n" : "") + "ENDPGM", null);
        var service = new CommandService(_system);
        Assert.False(service.Execute("CRTCLPGM PGM(QGPL/UPDATE) SRCFILE(QGPL/SOURCE)").IsError);
        Compile(D("DATA", "DS", keywords: "QUALIFIED INZ") + D("N", from: "1", length: "3", type: 'P', decimals: "0", keywords: "INZ(7)") +
            RpgFixedCalculationTests.C(fail ? "CALLP(E)" : "CALLP", expression: "UPDATE(DATA.N:DATA.N)") +
            RpgFixedCalculationTests.C("DSPLY", factor1: "DATA.N") + RpgFixedCalculationTests.C("SETON", eq: "LR"));
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "UPDATE");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "16");
    }

    [Theory]
    [InlineData(" ", "U")]
    public void Unsupported_native_data_area_structures_prevent_program_creation(string external, string status)
    {
        var result = Create(D("DATA", "DS", external: external[0], status: status[0]) + D("N", length: "1") + "/free\nreturn;\n/end-free");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
    [Fact]
    public void Native_external_headers_use_the_structure_name_as_the_saved_description()
    {
        Compile(D("DATA", "DS", external: 'E', keywords: "QUALIFIED INZ") + D("EXTRA", length: "1") + "/free\neval DATA.ID=77;\nsndpgmmsg %char(DATA.ID);\nreturn;\n/end-free");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.DeleteFile("QGPL", "DATA"); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "77");
    }

}
