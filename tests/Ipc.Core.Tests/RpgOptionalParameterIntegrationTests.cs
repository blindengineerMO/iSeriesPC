using Ipc.Core.Text;
using Ipc.Core.Work;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Saved_native_optional_signatures_keep_real_argument_counts_and_guard_unpassed_values(int ccsid, bool fixedForm)
    {
        var source = fixedForm ? D(definition: "PI") + D("N", length: "3", type: 'I', decimals: "0") + D("TEXT", length: "4", keywords: "CONST OPTIONS(*NOPASS)") + "**free\n" :
            "**free\ndcl-pi *n;\nN int(3);\nTEXT char(4) const options(*nopass);\nend-pi;\n";
        source += "dcl-ds PS qualified psds;\nCOUNT *parms;\nend-ds;\nif %passed(TEXT) and TEXT='ABCD';\neval N=N+10;\n" +
            "endif;\nif not %passed(TEXT) or TEXT='';\neval N=N+1;\nendif;\nsndpgmmsg %char(PS.COUNT)+':'+%char(%parmnum(TEXT));\nreturn;";
        Compile(source, "OPTPI", "OPTIONAL");
        source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('OPTPI')") + D(length: "3", type: 'I', decimals: "0") + D(length: "4", keywords: "CONST OPTIONS(*NOPASS)") + "**free\n" :
            "**free\ndcl-pr RUN extpgm('OPTPI');\nN int(3);\nTEXT char(4) const options(*nopass);\nend-pr;\n";
        source += "dcl-s N int(3) inz(0);\ncallp RUN(N:'ABCD');\ncallp RUN(N);\ncallp RUN(N:'ABCD');\nif N=21;\nsndpgmmsg 'OPTIONAL ALIASES OK';\nendif;\nseton LR;";
        Compile(source); foreach (var member in new[] { "APP", "OPTPI" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/APP"); Ok(result, "OPTIONAL ALIASES OK"); Assert.Contains("1:2", result.Message); Assert.Contains("2:2", result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CL_callers_can_omit_native_optional_parameters_and_pass_typed_constants(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nN int(10) options(*nopass);\nTEXT char(4) const options(*nopass);\nend-pi;\n" +
            "if %passed(N);\neval N=N+1;\nsndpgmmsg %char(N);\nendif;\nif %passed(TEXT);\nsndpgmmsg TEXT;\nendif;\n" +
            "sndpgmmsg 'COUNT='+%char(%parms());\nreturn;", "OPTPI", "OPTIONAL");
        ClExternalCallTests.Create(_system, "OPTCL", "CLP", "PGM\nDCL &N *INT VALUE(7)\nCALL OPTPI\nCALL OPTPI PARM(&N)\nCALL OPTPI PARM((12 (*INT 4)) ('ABCD' (*CHAR 4)))\nSNDPGMMSG MSG(&N)\nENDPGM");
        _files.RemoveMember("QGPL", "SOURCE", "OPTPI"); using var session = Session(ccsid); var result = session.Execute("CALL QGPL/OPTCL");
        Ok(result, "ABCD"); Assert.Contains("COUNT=0", result.Message); Assert.Contains("COUNT=1", result.Message); Assert.Contains("COUNT=2", result.Message); Assert.Contains("13", result.Message); Assert.EndsWith("8", result.Message?.Trim());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Unpassed_native_API_receiver_rejects_before_dequeue_even_with_a_guarded_monitor(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "OPTIONQ"), 8); queues.Send(new("QGPL", "OPTIONQ"), new ProgramBuffer(CodePage.ToBytes(ccsid, "READY"), ccsid));
        Compile("**free\ndcl-pi *n;\nDATA char(8) options(*nopass);\nend-pi;\ndcl-pr GET extpgm('QRCVDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0);\nDATA char(8);\nWAIT packed(5:0) const;\nend-pr;\n" +
            "dcl-s LEN packed(5:0) inz(99);\nmonitor;\ncallp(e) GET('OPTIONQ':'QGPL':LEN:DATA:0);\non-error 105;\nif LEN=99;\nsndpgmmsg 'UNPASSED RECEIVER REJECTED';\nendif;\nendmon;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "UNPASSED RECEIVER REJECTED"); Assert.Equal(1, queues.Inspect(new("QGPL", "OPTIONQ")).Entries);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Optional_native_prototypes_send_only_present_parameters_to_real_system_APIs(int ccsid)
    {
        var queues = new Ipc.Services.Messages.DataQueueStore(_system.Connections); queues.Create(new("QGPL", "OPTIONQ"), 8);
        Compile("**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(8) const;\nKEYLEN packed(3:0) const options(*nopass);\nKEY char(2) const options(*nopass);\nend-pr;\n" +
            "callp SEND('OPTIONQ':'QGPL':5:'HELLO');\nsndpgmmsg 'OPTIONAL API OK';\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "OPTIONAL API OK"); Assert.Equal("HELLO", queues.Receive(new("QGPL", "OPTIONQ"))!.Data.ToText());
    }
}
