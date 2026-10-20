using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class MemberRemovalSelectionTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public MemberRemovalSelectionTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        _files.CreateSourceFile("QGPL", "SOURCES");
        foreach (var member in new[] { "SRC", "SRCA", "SRCB", "OTHER" }) _files.AddMember("QGPL", "SOURCES", member);
    }

    [Theory]
    [InlineData("RMVM FILE(QGPL/SOURCES) MBR(src*)")]
    [InlineData("RMVM QGPL/SOURCES SRC*")]
    [InlineData("RMVM QGPL/SOURCES MBR(SRC*)")]
    [InlineData("RMVFM FILE(QGPL/SOURCES) MBR(SRC*)")]
    public void Generic_selection_removes_only_matching_members(string command)
    {
        var result = new CommandService(_system).Execute(command);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(new[] { "OTHER", "SOURCES" }, _files.ListMembers("QGPL", "SOURCES"));
        Assert.Contains("3 members removed", result.Message);
    }

    [Fact]
    public void All_selection_leaves_the_file_available_for_new_members()
    {
        var commands = new CommandService(_system);
        var result = commands.Execute("RMVM QGPL/SOURCES *ALL"); Assert.False(result.IsError, result.Message);
        Assert.Empty(_files.ListMembers("QGPL", "SOURCES")); Assert.True(_files.FileExists("QGPL", "SOURCES"));
        _files.AddMember("QGPL", "SOURCES", "REPLACED"); Assert.Equal("REPLACED", _files.FirstMember("QGPL", "SOURCES"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("SR*C")]
    [InlineData("SRC**")]
    [InlineData("*FIRST")]
    [InlineData("ABCDEFGHIJK*")]
    [InlineData("")]
    public void Invalid_or_missing_selectors_leave_all_members_intact(string selector)
    {
        var before = _files.ListMembers("QGPL", "SOURCES");
        var result = new CommandService(_system).Execute("RMVM FILE(QGPL/SOURCES)" + (selector.Length == 0 ? "" : " MBR(" + selector + ")"));
        Assert.True(result.IsError); Assert.Equal("IPC0003", result.MessageId);
        Assert.Equal(before, _files.ListMembers("QGPL", "SOURCES"));
    }

    [Fact]
    public void Unmatched_selection_reports_failure_without_removing_other_members()
    {
        var before = _files.ListMembers("QGPL", "SOURCES");
        var result = new CommandService(_system).Execute("RMVM FILE(QGPL/SOURCES) MBR(MISSING*)");
        Assert.True(result.IsError); Assert.Contains("No members match", result.Message);
        Assert.Equal(before, _files.ListMembers("QGPL", "SOURCES"));
    }

    [Fact]
    public void Generic_selection_continues_after_a_failed_member_and_retains_diagnostics()
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE VIEW block_srca AS SELECT * FROM \"QGPL.SOURCES.SRCA\""; command.ExecuteNonQuery();
        }
        var result = new CommandService(_system).Execute("RMVM FILE(QGPL/SOURCES) MBR(SRC*)");
        Assert.True(result.IsError); Assert.Equal("CPF7301", result.MessageId);
        Assert.Contains("SRCA", result.Message); Assert.Contains("IPC0201", result.Message); Assert.Contains("2 members removed", result.Message);
        Assert.Equal(new[] { "OTHER", "SOURCES", "SRCA" }, _files.ListMembers("QGPL", "SOURCES"));
    }

    [Fact]
    public void Generic_selection_requires_existence_and_operate_without_read_authority()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "MemberSelectionPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        var descriptor = _system.Objects.GetRequired("QGPL", "SOURCES", ObjectType.File);
        descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor);
        _system.Security.Authority.Grant("QGPL", "SOURCES", ObjectType.File, "QUSER", AuthorityBit.ObjectExist | AuthorityBit.ObjectOperate);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            var result = session.Execute("RMVM FILE(QGPL/SOURCES) MBR(SRC*)"); Assert.False(result.IsError, result.Message);
        }
        Assert.Equal(new[] { "OTHER", "SOURCES" }, _files.ListMembers("QGPL", "SOURCES"));
    }

    [Fact]
    public void Compiled_CL_monitors_partial_selection_failure_after_later_members_are_processed()
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "CREATE VIEW block_srca AS SELECT * FROM \"QGPL.SOURCES.SRCA\""; command.ExecuteNonQuery(); }
        _files.SaveSourceMember("QGPL", "SOURCES", "RMSELECT", """
            PGM
            RMVM QGPL/SOURCES SRC*
            MONMSG MSGID(CPF7301) EXEC(SNDPGMMSG MSG('PARTIAL RMVM HANDLED'))
            ENDPGM
            """, null);
        var commands = new CommandService(_system);
        var compile = commands.Execute("CRTCLPGM PGM(QGPL/RMSELECT) SRCFILE(QGPL/SOURCES)"); Assert.False(compile.IsError, compile.Message);
        var result = commands.Execute("CALL QGPL/RMSELECT"); Assert.False(result.IsError, result.Message);
        Assert.Contains("PARTIAL RMVM HANDLED", result.Message);
        Assert.True(_files.MemberExists("QGPL", "SOURCES", "SRCA"));
        Assert.False(_files.MemberExists("QGPL", "SOURCES", "SRC")); Assert.False(_files.MemberExists("QGPL", "SOURCES", "SRCB"));
    }

    public void Dispose() => _system.Dispose();
}
