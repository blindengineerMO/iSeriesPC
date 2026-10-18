using System.Text.Json.Nodes;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(false, "", 60)]
    [InlineData(false, "*IMMED", 0)]
    [InlineData(false, "*NOMAX", 32767)]
    [InlineData(false, "1", 1)]
    [InlineData(false, "32767", 32767)]
    [InlineData(true, "", 60)]
    [InlineData(true, "*IMMED", 0)]
    [InlineData(true, "*NOMAX", 32767)]
    [InlineData(true, "1", 1)]
    [InlineData(true, "32767", 32767)]
    public void Native_create_commands_persist_the_record_wait_default_and_supported_values(bool logical, string wait, int expected)
    {
        var source = logical ? LogicalFileTests.Line('R', "VROW", "PFILE(QGPL/DATA)") :
            NativePhysicalDdsTests.Line('R', "REC") + "\n" + NativePhysicalDdsTests.Line(' ', "VALUE", "5", 'A');
        _files.SaveSourceMember("QGPL", "SOURCE", "WAIT", source, null);
        var result = new CommandService(_system).Execute((logical ? "CRTLF" : "CRTPF") + " FILE(QGPL/WAIT) SRCFILE(QGPL/SOURCE)" + (wait.Length == 0 ? "" : " WAITRCD(" + wait + ")"));
        Assert.False(result.IsError, result.Message); Assert.Equal(expected, _files.GetDefinition("QGPL", "WAIT")!.RecordLockWaitSeconds);
    }
    [Fact]
    public void Old_definition_JSON_defaults_to_sixty_seconds_and_changes_need_no_RPG_recompile()
    {
        var json = JsonNode.Parse(_files.GetDefinition("QGPL", "DATA")!.ToJson())!.AsObject(); json.Remove("recordLockWaitSeconds");
        Assert.Equal(60, FileDefinition.FromJson(json.ToJsonString()).RecordLockWaitSeconds);
        Compile(UpdateFile() + "chain 20 DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        var changed = new CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) WAITRCD(7)"); Assert.False(changed.IsError, changed.Message);
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "101");
    }
    [Fact]
    public void CHGPF_changes_wait_and_member_limit_atomically_and_SAME_preserves_them()
    {
        _files.AddMember("QGPL", "DATA", "SECOND"); var commands = new CommandService(_system);
        Assert.True(commands.Execute("CHGPF FILE(QGPL/DATA) MAXMBRS(1) WAITRCD(7)").IsError);
        Assert.Equal(0, _files.GetDefinition("QGPL", "DATA")!.RecordLockWaitSeconds);
        Assert.False(commands.Execute("CHGPF FILE(QGPL/DATA) MAXMBRS(3) WAITRCD(7)").IsError);
        Assert.False(commands.Execute("CHGPF FILE(QGPL/DATA) WAITRCD(*SAME)").IsError);
        var definition = _files.GetDefinition("QGPL", "DATA")!; Assert.Equal(7, definition.RecordLockWaitSeconds); Assert.Equal(3, definition.MaximumMembers);
        Assert.False(commands.Execute("CHGPF FILE(QGPL/DATA) WAITRCD(*NOMAX)").IsError); Assert.Equal(32767, _files.GetDefinition("QGPL", "DATA")!.RecordLockWaitSeconds);
    }
    [Fact]
    public void CHGLF_changes_only_the_logical_wait_and_validates_the_file_type()
    {
        OutputView(); var commands = new CommandService(_system);
        Assert.False(commands.Execute("CHGLF FILE(QGPL/VIEW) WAITRCD(4)").IsError);
        Assert.False(commands.Execute("CHGLF FILE(QGPL/VIEW) WAITRCD(*SAME)").IsError); Assert.Equal(4, _files.GetDefinition("QGPL", "VIEW")!.RecordLockWaitSeconds);
        Assert.False(commands.Execute("CHGLF FILE(QGPL/VIEW) WAITRCD(*NOMAX)").IsError); Assert.Equal(32767, _files.GetDefinition("QGPL", "VIEW")!.RecordLockWaitSeconds);
        Assert.Equal(0, _files.GetDefinition("QGPL", "DATA")!.RecordLockWaitSeconds);
        Assert.True(commands.Execute("CHGLF FILE(QGPL/DATA) WAITRCD(4)").IsError); Assert.True(commands.Execute("CHGPF FILE(QGPL/VIEW) WAITRCD(4)").IsError);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("32768")]
    [InlineData("1.5")]
    [InlineData("*FILE")]
    [InlineData("*SAME")]
    public void Invalid_create_and_override_wait_values_do_not_change_existing_state(string value)
    {
        using var session = Session(); var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        var overridden = session.Execute("OVRDBF FILE(DATA) WAITRCD(" + value + ")"); Assert.True(overridden.IsError);
        Assert.Empty(_system.JobRuntime.Environment(session.Job.Key)!.Overrides());
        var created = session.Execute("CRTPF FILE(QGPL/BADWAIT) SRCFILE(QGPL/SOURCE) SRCMBR(NOFILE) WAITRCD(" + value + ")"); Assert.True(created.IsError);
        Assert.False(_files.FileExists("QGPL", "BADWAIT")); Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
    }
    [Fact]
    public void Record_wait_changes_require_live_alter_and_management_authority()
    {
        OutputView(); _system.Security.Profiles.Create(new() { Name = "WAITUSER" });
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "WAITUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "VIEW", ObjectType.File, "WAITUSER", Authorities.UseBits);
        using var identity = OperationIdentity.Enter("WAITUSER");
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, 9)).MessageId);
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ChangeLogicalRecordWait("QGPL", "VIEW", 9)).MessageId);
    }
    [Fact]
    public void OVRDBF_and_DSPOVR_preserve_wait_values_and_call_scope_unwinds_to_the_job_policy()
    {
        using var session = Session(); var environment = _system.JobRuntime.Environment(session.Job.Key)!;
        Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(*FILE) WAITRCD(2) OVRSCOPE(*JOB)").IsError);
        Assert.Equal(2, environment.Resolve("DATA")!.RecordLockWaitSeconds);
        using (environment.EnterCall("NESTED"))
        {
            Assert.False(session.Execute("OVRDBF FILE(DATA) WAITRCD(*NOMAX) OVRSCOPE(*CALLLVL)").IsError);
            Assert.Equal(-1, environment.Resolve("DATA")!.RecordLockWaitSeconds);
            Assert.Contains(session.Execute("DSPOVR FILE(DATA)").Listing!, row => row.Contains("WAITRCD(*NOMAX)"));
        }
        Assert.Equal(2, environment.Resolve("DATA")!.RecordLockWaitSeconds);
        Assert.False(session.Execute("DLTOVR FILE(DATA) LVL(*JOB)").IsError); Assert.Null(environment.Resolve("DATA"));
    }
    [Fact]
    public void Malformed_wait_metadata_and_override_objects_are_rejected_before_open()
    {
        var definition = _files.GetDefinition("QGPL", "DATA")!; definition.RecordLockWaitSeconds = -1;
        Assert.Throws<CpfException>(() => RpgDiskLayouts.RecordWait(definition));
        definition.RecordLockWaitSeconds = 60; Assert.Equal(TimeSpan.FromSeconds(60), RpgDiskLayouts.RecordWait(definition));
        Assert.Equal(Timeout.InfiniteTimeSpan, RpgDiskLayouts.RecordWait(definition, -1));
        using var session = Session(); var environment = _system.JobRuntime.Environment(session.Job.Key)!;
        Assert.Throws<CpfException>(() => environment.Override(new("DATA", "QGPL", "DATA", "DATA", false, JobEnvironmentScope.Job, -2)));
        Assert.Throws<CpfException>(() => environment.Override(new("DATA", "QGPL", "DATA", "DATA", false, JobEnvironmentScope.Job, 32768)));
        Assert.Empty(environment.Overrides());
    }
}
