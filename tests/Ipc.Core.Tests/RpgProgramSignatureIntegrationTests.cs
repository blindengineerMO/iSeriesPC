using Ipc.Core.Objects;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false, false)] [InlineData(1208, false, false)]
    [InlineData(37, true, false)] [InlineData(1208, true, false)]
    [InlineData(37, false, true)] [InlineData(1208, false, true)]
    [InlineData(37, true, true)] [InlineData(1208, true, true)]
    public void Saved_native_program_prototypes_and_main_interfaces_keep_live_one_byte_aliases_in_real_jobs(int ccsid, bool fixedForm, bool fail)
    {
        var callee = fixedForm ? D(definition: "PI") + D("A", length: "3", type: 'I', decimals: "0") + D("B", length: "3", type: 'I', decimals: "0") + "**free\n" :
            "**free\ndcl-pi *n;\nA int(3);\nB int(3);\nend-pi;\n";
        callee += "eval A=A+1;\neval B=A+B;\n" + (fail ? "eval A=1/0;\n" : "") + "seton LR;";
        Compile(callee, "SIGCALLEE");
        var caller = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('QGPL/SIGCALLEE')") + D(length: "3", type: 'I', decimals: "0") + D(length: "3", type: 'I', decimals: "0") + "**free\n" :
            "**free\ndcl-pr RUN extpgm('QGPL/SIGCALLEE');\nFIRST int(3);\nSECOND int(3);\nend-pr;\n";
        caller += "dcl-s N int(3) inz(7);\ncallp(e) RUN(N:N);\nif N=16 and %error()=" + (fail ? "*on" : "*off") + ";\nsndpgmmsg 'NATIVE SIGNATURE OK';\nendif;\nseton LR;";
        Compile(caller); _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "SIGCALLEE");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NATIVE SIGNATURE OK");
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_program_prototypes_pass_live_packed_aliases_to_CL_and_keep_writes_after_escape(int ccsid, bool fail)
    {
        ClExternalCallTests.Create(_system, "SIGCL", "CLP", "PGM PARM(&A &B)\nDCL &A *DEC LEN(5 0)\nDCL &B *DEC LEN(5 0)\nCHGVAR &A (&A + 1)\nCHGVAR &B (&A + &B)\n" +
            (fail ? "SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('EXPECTED') MSGTYPE(*ESCAPE)\n" : "") + "ENDPGM");
        Compile("**free\ndcl-pr RUN extpgm('SIGCL');\nA packed(5:0);\nB packed(5:0);\nend-pr;\ndcl-s N packed(5:0) inz(7);\ncallp(e) RUN(N:N);\n" +
            "if N=16 and %error()=" + (fail ? "*on" : "*off") + ";\nsndpgmmsg 'NATIVE CL SIGNATURE OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NATIVE CL SIGNATURE OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_program_prototypes_borrow_exact_API_receivers_without_binding_prototype_variables(int ccsid)
    {
        Compile("**free\ndcl-pr GETVAL extpgm('QSYS/QWCRSVAL');\nRECEIVER char(64) ccsid(*hex);\nSIZE int(10);\nCOUNT int(10);\nNAMES char(10);\nERROR char(4) ccsid(*hex);\nend-pr;\n" +
            "dcl-s INFO char(64) ccsid(*hex);\ndcl-s BYTES int(10) inz(64);\ndcl-s COUNT int(10) inz(1);\ndcl-s NAMES char(10) inz('QCCSID');\ndcl-s ERROR char(4) ccsid(*hex) inz(X'00000000');\n" +
            "callp GETVAL(INFO:BYTES:COUNT:NAMES:ERROR);\nif %subst(INFO:1:8)=X'0000000100000008' and %subst(INFO:25:4)=X'00000025';\nsndpgmmsg 'NATIVE PROTOTYPE API OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NATIVE PROTOTYPE API OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_native_PI_calls_write_all_scalar_formats_into_the_callers_DS_byte_image(int ccsid)
    {
        var parameters = "TEXT char(2);\nNUMBER packed(5:1);\nZONE zoned(3:1);\nSMALL int(3);\nREAL float(4);\nFLAG ind;\nDAY date;\nCLOCK time;\nSTAMP timestamp;\n";
        Compile("**free\ndcl-pi *n;\n" + parameters + "end-pi;\neval TEXT='CD';\neval NUMBER=123.4;\neval ZONE=-12.3;\neval SMALL=-7;\neval REAL=1.5;\neval FLAG=*on;\n" +
            "eval DAY='2026-10-18';\neval CLOCK='12:34:56';\neval STAMP='2026-10-18T12:34:56.123456';\nseton LR;", "SIGTYPES");
        var expected = Convert.ToHexString(Ipc.Core.Text.CodePage.ToBytes(ccsid, "CD")) + "01234CF1F2D3F93FC00000" +
            Convert.ToHexString(Ipc.Core.Text.CodePage.ToBytes(ccsid, "12026-10-1812.34.562026-10-18-12.34.56.123456"));
        Compile("**free\ndcl-pr RUN extpgm('SIGTYPES');\n" + parameters + "end-pr;\ndcl-ds STATE qualified inz;\n" + parameters + "end-ds;\n" +
            "callp RUN(STATE.TEXT:STATE.NUMBER:STATE.ZONE:STATE.SMALL:STATE.REAL:STATE.FLAG:STATE.DAY:STATE.CLOCK:STATE.STAMP);\n" +
            "if %subst(STATE:1:58)=X'" + expected + "';\nsndpgmmsg 'NATIVE PI BYTES OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "SIGTYPES");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NATIVE PI BYTES OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Native_PI_rebinds_retained_activations_and_reports_parameter_count_in_PSDS(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nN int(10);\nend-pi;\ndcl-s CALLS int(10) inz(0);\ndcl-ds PS qualified psds;\nCOUNT *parms;\nend-ds;\n" +
            "eval CALLS=CALLS+1;\neval N=N+CALLS;\nsndpgmmsg %char(N)+':'+%char(PS.COUNT);\nreturn;", "SIGRET", "SIGNATURE");
        ClExternalCallTests.Create(_system, "SIGWRAP", "CLP", "PGM\nDCL &N *INT VALUE(7)\nCALL SIGRET PARM(&N)\nSNDPGMMSG MSG(&N)\nENDPGM");
        _files.RemoveMember("QGPL", "SOURCE", "SIGRET"); using var session = Session(ccsid);
        Ok(session.Execute("CALL QGPL/SIGWRAP"), "8:1"); Ok(session.Execute("CALL QGPL/SIGWRAP"), "9:1");
        Ok(session.Execute("RCLACTGRP ACTGRP(SIGNATURE)"), ""); Ok(session.Execute("CALL QGPL/SIGWRAP"), "8:1");
    }

    [Theory]
    [InlineData("CONST(7)")] [InlineData("OPTIONS(*OMIT:*OMIT)")] [InlineData("DIM(*VAR)")]
    public void Unsupported_native_parameter_modes_prevent_saved_program_creation(string option)
    {
        var result = Create(D("RUN", "PR", keywords: "EXTPGM('TARGET')") + D("N", length: "3", type: 'I', decimals: "0", keywords: option) + "**free\nreturn;");
        Assert.True(result.IsError); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
}
