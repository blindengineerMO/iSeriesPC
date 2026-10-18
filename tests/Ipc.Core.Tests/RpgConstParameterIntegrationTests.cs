using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Messages;
using static Ipc.Core.Tests.RpgFixedStructureTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Native_CONST_literals_call_real_queue_APIs_after_saved_source_removal(int ccsid, bool fixedForm)
    {
        var queues = new DataQueueStore(_system.Connections); queues.Create(new("QGPL", "CONSTQ"), 8);
        var source = fixedForm ? D("SEND", "PR", keywords: "EXTPGM('QSNDDTAQ')") + D(length: "10", keywords: "CONST") + D(length: "10", keywords: "CONST") +
            D(length: "5", type: 'P', decimals: "0", keywords: "CONST") + D(length: "8", keywords: "CONST") + "**free\n" :
            "**free\ndcl-pr SEND extpgm('QSNDDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0) const;\nDATA char(8) const;\nend-pr;\n";
        source += "callp SEND('CONSTQ':'QGPL':5:'HELLO');\nsndpgmmsg 'CONST API OK';\nseton LR;";
        Compile(source); _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "CONST API OK");
        Assert.Equal("HELLO", queues.Receive(new("QGPL", "CONSTQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void CONST_receiver_rejection_preserves_the_queue_entry_and_every_other_output(int ccsid)
    {
        var queues = new DataQueueStore(_system.Connections); queues.Create(new("QGPL", "CONSTQ"), 8);
        queues.Send(new("QGPL", "CONSTQ"), new ProgramBuffer(CodePage.ToBytes(ccsid, "READY"), ccsid));
        Compile("**free\ndcl-pr GET extpgm('QRCVDTAQ');\nQ char(10) const;\nLIB char(10) const;\nLEN packed(5:0);\nDATA char(8) const;\nWAIT packed(5:0) const;\nend-pr;\n" +
            "dcl-s LEN packed(5:0) inz(99);\ndcl-s DATA char(8) inz('TAILTAIL');\ncallp(e) GET('CONSTQ':'QGPL':LEN:DATA:0);\n" +
            "if %error() and LEN=99 and DATA='TAILTAIL';\nsndpgmmsg 'CONST RECEIVER REJECTED';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "CONST RECEIVER REJECTED");
        Assert.Equal(1, queues.Inspect(new("QGPL", "CONSTQ")).Entries); Assert.Equal("READY", queues.Receive(new("QGPL", "CONSTQ"))!.Data.ToText());
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_CONST_PI_observes_its_mutable_alias_without_permitting_assignment(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nINPUT char(4) const;\nOUTPUT char(4);\nend-pi;\neval OUTPUT='WXYZ';\nif INPUT='WXYZ';\nsndpgmmsg 'CONST LIVE ALIAS OK';\nendif;\nseton LR;", "CONSTPI");
        Compile("**free\ndcl-pr RUN extpgm('CONSTPI');\nINPUT char(4) const;\nOUTPUT char(4);\nend-pr;\ndcl-s TEXT char(4) inz('ABCD');\ncallp RUN(TEXT:TEXT);\n" +
            "if TEXT='WXYZ';\nsndpgmmsg 'CONST CALLER OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "CONSTPI"); using var session = Session(ccsid);
        var result = session.Execute("CALL QGPL/APP"); Ok(result, "CONST LIVE ALIAS OK"); Assert.Contains("CONST CALLER OK", result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Saved_numeric_CONST_conversions_remain_private_from_a_mutable_actual(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nINPUT packed(5:1) const;\nOUTPUT zoned(7:3);\nend-pi;\neval OUTPUT=99.125;\nif INPUT=42.1;\nsndpgmmsg 'CONST COPY OK';\nendif;\nseton LR;", "CONSTPI");
        Compile("**free\ndcl-pr RUN extpgm('CONSTPI');\nINPUT packed(5:1) const;\nOUTPUT zoned(7:3);\nend-pr;\ndcl-s N zoned(7:3) inz(42.125);\ncallp RUN(N:N);\n" +
            "if N=99.125;\nsndpgmmsg 'CONST OUTPUT OK';\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.RemoveMember("QGPL", "SOURCE", "CONSTPI"); using var session = Session(ccsid);
        var result = session.Execute("CALL QGPL/APP"); Ok(result, "CONST COPY OK"); Assert.Contains("CONST OUTPUT OK", result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Nested_native_character_prefixes_write_only_the_shared_prefix_and_preserve_parent_tails(int ccsid)
    {
        Compile("**free\ndcl-pi *n;\nTEXT char(2);\nend-pi;\neval TEXT='XY';\nseton LR;", "CONSTSUB");
        Compile("**free\ndcl-pi *n;\nTEXT char(4);\nend-pi;\ndcl-pr RUN extpgm('CONSTSUB');\nINPUT char(2);\nend-pr;\ncallp RUN(TEXT);\nseton LR;", "CONSTPI");
        Compile("**free\ndcl-pr RUN extpgm('CONSTPI');\nINPUT char(4);\nend-pr;\ndcl-s TEXT char(6) inz('ABCDEF');\ncallp RUN(TEXT);\nif TEXT='XYCDEF';\nsndpgmmsg 'PREFIX TAIL OK';\nendif;\nseton LR;");
        foreach (var member in new[] { "APP", "CONSTPI", "CONSTSUB" }) _files.RemoveMember("QGPL", "SOURCE", member);
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "PREFIX TAIL OK");
    }
}
