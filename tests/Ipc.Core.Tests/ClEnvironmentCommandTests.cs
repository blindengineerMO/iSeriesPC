using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClEnvironmentCommandTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public ClEnvironmentCommandTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "LibraryFixture9");
        foreach (var library in new[] { "FIRST", "SECOND", "THIRD", "FOURTH" }) _system.Libraries.CreateLibrary(library);
    }
    private ExecutionSession Session(string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user, currentLibrary: "QGPL", libraryList: "FIRST SECOND"), CancellationToken.None);
    private static void Execute(ExecutionSession session, string command)
    { var result = session.Execute(command); Assert.False(result.IsError, result.Message); }
    private void Program(string name, string body) => ClExternalCallTests.Create(_system, name, "CLP", "PGM\n" + body + "\nENDPGM");

    [Theory]
    [InlineData("*FIRST", "THIRD FIRST SECOND")]
    [InlineData("*LAST", "FIRST SECOND THIRD")]
    [InlineData("*BEFORE SECOND", "FIRST THIRD SECOND")]
    [InlineData("*AFTER FIRST", "FIRST THIRD SECOND")]
    [InlineData("*REPLACE FIRST", "THIRD SECOND")]
    public void Add_library_honors_all_supported_positions(string position, string expected)
    {
        using var session = Session(); Execute(session, "ADDLIBLE THIRD (" + position + ")");
        Assert.Equal(expected, session.Job.LibraryList); Assert.Equal(expected, _system.Jobs.GetRequired(session.Job.Key).LibraryList);
        Execute(session, "RMVLIBLE THIRD"); Assert.DoesNotContain("THIRD", session.Job.LibraryList);
    }
    [Fact]
    public void Change_library_list_preserves_defaults_and_supports_no_current_library()
    {
        using var session = Session(); Execute(session, "CHGLIBL"); Assert.Equal("FIRST SECOND", session.Job.LibraryList);
        Execute(session, "CHGLIBL (THIRD FOURTH) SECOND"); Assert.Equal("THIRD FOURTH", session.Job.LibraryList); Assert.Equal("SECOND", session.Job.CurrentLibrary);
        Execute(session, "CHGLIBL LIBL(*NONE) CURLIB(*CRTDFT)"); Assert.Equal("", session.Job.LibraryList); Assert.Equal("*CRTDFT", session.Job.CurrentLibrary);
        Assert.DoesNotContain("QGPL", _system.SearchLibraries(session.Job)); Assert.Equal("QGPL", Assert.Single(_system.SearchLibraries(session.Job, "*CURLIB")));
        Execute(session, "CHGCURLIB FIRST"); Assert.Equal("FIRST", session.Job.CurrentLibrary);
    }
    [Fact]
    public void Invalid_library_change_is_atomic_in_memory_and_catalog()
    {
        using var session = Session();
        foreach (var command in new[] { "CHGLIBL LIBL(THIRD MISSING) CURLIB(SECOND)", "CHGLIBL LIBL(FIRST FIRST)", "ADDLIBLE THIRD POSITION(*AFTER MISSING)", "ADDLIBLE THIRD POSITION(*LAST FIRST)", "ADDLIBLE FIRST" })
        {
            Assert.True(session.Execute(command).IsError, command); Assert.Equal("FIRST SECOND", session.Job.LibraryList); Assert.Equal("QGPL", session.Job.CurrentLibrary);
            Assert.Equal("FIRST SECOND", _system.Jobs.GetRequired(session.Job.Key).LibraryList);
        }
    }
    [Fact]
    public void Adding_unauthorized_library_does_not_change_either_library_list_component()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QSYS", "THIRD", "*LIB", "READER", AuthorityBit.ObjectOperate);
        using var session = Session("READER");
        var result = session.Execute("CHGLIBL LIBL(FIRST THIRD) CURLIB(SECOND)");
        Assert.True(result.IsError); Assert.Equal("FIRST SECOND", session.Job.LibraryList); Assert.Equal("QGPL", session.Job.CurrentLibrary);
    }
    [Fact]
    public void Retrieval_returns_job_identity_library_lists_and_numeric_ccsid_to_typed_variables()
    {
        Program("ATTRS", "DCL &USER *CHAR LEN(10)\nDCL &CURRENT *CHAR LEN(10)\nDCL &LIST *CHAR LEN(2750)\nDCL &SYS *CHAR LEN(165)\nDCL &CCSID *DEC LEN(5 0)\n" +
            "CHGLIBL LIBL(FIRST SECOND) CURLIB(*CRTDFT)\nRTVJOBA USER(&USER) CURLIB(&CURRENT) USRLIBL(&LIST) SYSLIBL(&SYS) CCSID(&CCSID)\n" +
            "IF (&LIST *NE 'FIRST      SECOND     ') THEN(SNDPGMMSG MSGID(CPF9898) MSGF(QCPFMSG) MSGDTA('Wrong library fields') MSGTYPE(*ESCAPE))\nSNDPGMMSG MSG(&USER *TCAT ':' *CAT &CURRENT *TCAT ':' *CAT &CCSID)");
        using var session = Session(); var result = session.Execute("CALL QGPL/ATTRS");
        Assert.False(result.IsError, result.Message); Assert.Equal("QSECOFR:*NONE:37", result.Message?.Trim());
    }
    [Fact]
    public void Bad_later_return_variable_leaves_earlier_outputs_unchanged()
    {
        Program("BADOUT", "DCL &USER *CHAR LEN(10) VALUE('ORIGINAL')\nDCL &LIST *CHAR LEN(10)\nRTVJOBA USER(&USER) USRLIBL(&LIST)\nMONMSG IPC0003\nSNDPGMMSG MSG(&USER)");
        using var session = Session(); var result = session.Execute("CALL QGPL/BADOUT");
        Assert.False(result.IsError, result.Message); Assert.Equal("ORIGINAL", result.Message?.Trim());
    }
    [Fact]
    public void Data_area_retrieval_uses_byte_ranges_variables_and_padding_without_silent_truncation()
    {
        Program("AREA", "DCL &POS *INT VALUE(2)\nDCL &TEXT *CHAR LEN(3) VALUE('ABC')\nDCL &OUT *CHAR LEN(5)\n" +
            "CHGDTAARA DTAARA(*LDA (&POS 3)) VALUE(&TEXT)\nRTVDTAARA DTAARA(*LDA (&POS 3)) RTNVAR(&OUT)\n" +
            "RTVDTAARA DTAARA(*LDA *ALL) RTNVAR(&OUT)\nMONMSG IPC0003\nSNDPGMMSG MSG(&OUT *CAT '!')");
        using var session = Session(); var result = session.Execute("CALL QGPL/AREA");
        Assert.False(result.IsError, result.Message); Assert.Equal("ABC  !", result.Message?.Trim());
    }
    [Fact]
    public void Retrieval_requires_command_authority_and_a_compiled_variable_frame()
    {
        Program("ATTRS", "DCL &USER *CHAR LEN(10)\nRTVJOBA USER(&USER)\nSNDPGMMSG MSG(&USER)");
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _system.Security.Authority.Grant("QSYS", "RTVJOBA", "*CMD", "READER", AuthorityBit.None);
        using var session = Session("READER"); Assert.True(session.Execute("CALL QGPL/ATTRS").IsError);
        using var admin = Session(); Assert.True(admin.Execute("RTVJOBA USER(&USER)").IsError);
    }
    [Fact]
    public void Explicit_profile_default_creation_library_survives_signon_without_a_current_entry()
    {
        using (var session = Session()) Execute(session, "CHGUSRPRF USRPRF(QUSER) CURLIB(*CRTDFT)");
        var profile = _system.Security.Profiles.Get("QUSER"); Assert.Equal("*CRTDFT", profile.InitialCurrentLibrary);
        using var menu = new MenuController(_system, profile);
        Assert.Equal("*CRTDFT", _system.Jobs.GetRequired(menu.JobKey).CurrentLibrary);
    }
    [Fact]
    public void User_command_with_a_retrieval_name_keeps_its_CPP_dispatch()
    {
        Program("CUSTOM", "SNDPGMMSG MSG('CUSTOM COMMAND')");
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile("CMD", "QGPL/CUSTOM");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("FIRST", "RTVJOBA", "CMD", definition, "QSECOFR");
        Program("CALLER", "FIRST/RTVJOBA");
        using var session = Session(); var result = session.Execute("CALL QGPL/CALLER");
        Assert.False(result.IsError, result.Message); Assert.Equal("CUSTOM COMMAND", result.Message);
    }
    public void Dispose() => _system.Dispose();
}
