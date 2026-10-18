using Ipc.Core.Messages;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private const string NativeStructureApiSource = """
        **free
        dcl-ds INFO qualified len(128);
          COUNT int(10) pos(1);
          OFFSET int(10) pos(5);
          CCSID int(10) pos(33);
          SECURITY char(2) pos(53);
          PASSWORD_LEVEL int(10) pos(73);
        end-ds;
        dcl-ds ERROR qualified inz len(16);
          PROVIDED int(10) inz(16);
          AVAILABLE int(10);
          IDENTIFIER char(7);
        end-ds;
        callp QWCRSVAL(INFO:%size(INFO):3:'QCCSID    QSECURITY QPWDLVL   ':ERROR);
        if INFO.COUNT=3 and INFO.OFFSET=16 and INFO.CCSID=37 and INFO.SECURITY='40' and INFO.PASSWORD_LEVEL=3 and ERROR.AVAILABLE=0;
          sndpgmmsg 'NATIVE DS STORAGE ACCEPTED';
        endif;
        seton LR;
        """;

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_native_structures_receive_binary_API_headers_and_job_CCSID_character_fields(int ccsid)
    {
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        Compile(NativeStructureApiSource);
        _files.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = Session(ccsid); Assert.Equal(ccsid, session.Job.Ccsid);
        Ok(session.Execute("CALL QGPL/APP"), "NATIVE DS STORAGE ACCEPTED");
    }
    [Fact]
    public void A_retained_activation_keeps_native_data_structure_fields_between_calls()
    {
        Compile("**free\ndcl-ds STATE qualified inz;\nCOUNT int(10);\nend-ds;\neval STATE.COUNT=STATE.COUNT+1;\n" +
            "sndpgmmsg %char(STATE.COUNT);\nif STATE.COUNT<2;\nreturn;\nendif;\nseton LR;", group: "NATIVED");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "1"); Ok(session.Execute("CALL QGPL/APP"), "2");
    }
}
