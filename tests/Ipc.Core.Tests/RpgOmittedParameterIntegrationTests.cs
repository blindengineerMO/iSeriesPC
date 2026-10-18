using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Messages;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Saved_OMIT_signatures_distinguish_present_omitted_and_unpassed_values_after_source_removal(int ccsid, bool fixedForm)
    {
        var source = fixedForm ? D(definition: "PI") + D("N", length: "3", type: 'I', decimals: "0") + D("TEXT", length: "4", keywords: "CONST OPTIONS(*OMIT:*NOPASS)") + "**free\n" :
            "**free\ndcl-pi *n;\nN int(3);\nTEXT char(4) const options(*nopass:*omit);\nend-pi;\n";
        source += "dcl-ds PS qualified psds;\nCOUNT *parms;\nend-ds;\nif %passed(TEXT) and TEXT='ABCD';\neval N=N+10;\nendif;\n" +
            "if %omitted(TEXT);\neval N=N+2;\nendif;\nif not %passed(TEXT) and not %omitted(TEXT);\neval N=N+1;\nendif;\n" +
            "sndpgmmsg %char(PS.COUNT)+':'+%char(%parmnum(TEXT));\nreturn;";
        Compile(source, "OMITPI", "OMISSION");
        source = fixedForm ? D("RUN", "PR", keywords: "EXTPGM('OMITPI')") + D(length: "3", type: 'I', decimals: "0") + D(length: "4", keywords: "CONST OPTIONS(*NOPASS:*OMIT)") + "**free\n" :
            "**free\ndcl-pr RUN extpgm('OMITPI');\nN int(3);\nTEXT char(4) const options(*omit:*nopass);\nend-pr;\n";
        source += "dcl-s N int(3) inz(0);\ncallp RUN(N:'ABCD');\ncallp RUN(N:*omit);\ncallp RUN(N);\ncallp RUN(N:'ABCD');\nif N=23;\nsndpgmmsg 'OMITTED STATES OK';\nendif;\nseton LR;";
        Compile(source); foreach (var member in new[] { "APP", "OMITPI" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); var result = session.Execute("CALL QGPL/APP"); Ok(result, "OMITTED STATES OK"); Assert.Contains("1:2", result.Message); Assert.Contains("2:2", result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_saved_programs_can_forward_an_explicit_OMIT_and_keep_mutable_reference_writes(int ccsid)
    {
        const string formal = "N int(3);\nTEXT char(4) options(*omit);\n";
        Compile("**free\ndcl-pi *n;\n" + formal + "end-pi;\nif %omitted(TEXT) and not %passed(TEXT) and %parms()=2;\neval N=N+1;\nendif;\nseton LR;", "OMITSUB");
        Compile("**free\ndcl-pi *n;\n" + formal + "end-pi;\ndcl-pr RUN extpgm('OMITSUB');\n" + formal + "end-pr;\nif %omitted(TEXT);\ncallp RUN(N:*omit);\nendif;\nseton LR;", "OMITPI");
        Compile("**free\ndcl-pr RUN extpgm('OMITPI');\n" + formal + "end-pr;\ndcl-s N int(3) inz(7);\ncallp RUN(N:*omit);\nif N=8;\nsndpgmmsg 'NESTED OMIT OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "OMITPI", "OMITSUB" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "NESTED OMIT OK");
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Omitted_API_storage_rejects_before_queue_mutation_and_preserves_other_outputs(int ccsid, bool receiver)
    {
        var queues = new DataQueueStore(_system.Connections); queues.Create(new("QGPL", "OMITQ"), 8); queues.Send(new("QGPL", "OMITQ"), new ProgramBuffer(CodePage.ToBytes(ccsid, "READY"), ccsid));
        Compile("**free\ndcl-pr RUN extpgm('" + (receiver ? "QRCVDTAQ" : "QSNDDTAQ") + "');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0)" + (receiver ? "" : " const") + ";\nDATA char(8) options(*omit);\n" +
            (receiver ? "WAIT packed(5:0) const;\n" : "") + "end-pr;\ndcl-s LEN packed(5:0) inz(99);\ncallp(e) RUN('OMITQ':'QGPL':" + (receiver ? "LEN" : "5") + ":*omit" + (receiver ? ":0" : "") + ");\n" +
            "if %error() and LEN=99;\nsndpgmmsg 'OMITTED API STORAGE REJECTED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "OMITTED API STORAGE REJECTED"); Assert.Equal(1, queues.Inspect(new("QGPL", "OMITQ")).Entries);
        Assert.Equal("READY", queues.Receive(new("QGPL", "OMITQ"))!.Data.ToText());
    }
}
