using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Db.Dds;
using Ipc.Db.Store;
using Ipc.Services;
using static Ipc.Core.Tests.NativePhysicalDdsTests;

namespace Ipc.Core.Tests;

public sealed class PhysicalMemberTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly string _source = Line('R', "REC") + "\n" + Line(' ', "VALUE", "5", 'A');
    public PhysicalMemberTests() { _system.Start(); _files = new(_system.Connections, _system.Objects); }

    [Fact]
    public void CRTPF_none_named_default_and_maximum_members_are_enforced_through_commands()
    {
        _files.CreateSourceFile("QGPL", "DDS"); _files.SaveSourceMember("QGPL", "DDS", "DATA", _source, null);
        var commands = new CommandService(_system);
        var create = commands.Execute("CRTPF FILE(QGPL/DATA) SRCFILE(QGPL/DDS) MBR(*NONE) MAXMBRS(2)"); Assert.False(create.IsError, create.Message);
        Assert.Empty(_files.ListMembers("QGPL", "DATA"));
        Assert.False(commands.Execute("ADDPFM FILE(QGPL/DATA) MBR(FIRST)").IsError);
        Assert.True(commands.Execute("ADDPFM FILE(QGPL/DATA) MBR(FIRST)").IsError);
        Assert.False(commands.Execute("ADDPFM FILE(QGPL/DATA) MBR(SECOND)").IsError);
        Assert.True(commands.Execute("ADDPFM FILE(QGPL/DATA) MBR(THIRD)").IsError);
        Assert.False(_files.MemberExists("QGPL", "DATA", "THIRD"));
        Assert.False(commands.Execute("RMVM FILE(QGPL/DATA) MBR(FIRST)").IsError);
        Assert.False(commands.Execute("ADDPFM FILE(QGPL/DATA) MBR(THIRD)").IsError);
        Assert.False(commands.Execute("CRTPF FILE(QGPL/NAMED) SRCFILE(QGPL/DDS) SRCMBR(DATA) MBR(CHOSEN)").IsError);
        Assert.Equal("CHOSEN", _files.FirstMember("QGPL", "NAMED"));
        Assert.True(commands.Execute("ADDPFM FILE(QGPL/NAMED) MBR(SECOND)").IsError);
        Assert.False(commands.Execute("CRTPF FILE(QGPL/OPEN) SRCFILE(QGPL/DDS) SRCMBR(DATA) MAXMBRS(*NOMAX)").IsError);
        Assert.Equal(32767, _files.GetDefinition("QGPL", "OPEN")!.MaximumMembers);
        Assert.True(commands.Execute("CRTPF FILE(QGPL/BAD) SRCFILE(QGPL/DDS) SRCMBR(DATA) MAXMBRS(32768)").IsError);
        Assert.False(_files.FileExists("QGPL", "BAD"));
    }

    [Fact]
    public async Task Concurrent_member_creation_never_exceeds_the_persisted_limit()
    {
        _files.CreatePhysicalFile("QGPL", "DATA", new DdsCompiler().CompilePhysical("DATA", _source), _source, member: "*NONE", maximumMembers: 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => {
            try { _files.AddMember("QGPL", "DATA", "MBR" + i); return true; }
            catch (CpfException error) when (error.MessageId == "IPC0138") { return false; }
        })));
        Assert.Equal(2, results.Count(success => success)); Assert.Equal(2, _files.ListMembers("QGPL", "DATA").Count);
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name GLOB 'QGPL.DATA.MBR*'";
        Assert.Equal(2L, query.ExecuteScalar());
    }

    [Fact]
    public void Source_save_cannot_bypass_member_limits_or_change_existing_data_on_duplicate_add()
    {
        _files.CreateSourceFile("QGPL", "DDS");
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_file_defs SET def=json_set(def,'$.maximumMembers',1) WHERE lib='QGPL' AND name='DDS'"; command.ExecuteNonQuery();
        Assert.Throws<CpfException>(() => _files.SaveSourceMember("QGPL", "DDS", "EXTRA", _source, null));
        Assert.False(_files.MemberExists("QGPL", "DDS", "EXTRA"));
        var previous = _files.ReadSourceMember("QGPL", "DDS", "DDS");
        _files.SaveSourceMember("QGPL", "DDS", "DDS", _source, previous.Revision);
        Assert.Equal("CPF5812", Assert.Throws<CpfException>(() => _files.AddMember("QGPL", "DDS", "DDS")).MessageId);
        Assert.Contains("VALUE", _files.ReadSourceMember("QGPL", "DDS", "DDS").Source);
    }

    [Fact]
    public void CHGPF_validates_current_count_and_live_authority_before_changing_metadata()
    {
        _files.CreatePhysicalFile("QGPL", "DATA", new DdsCompiler().CompilePhysical("DATA", _source), _source, maximumMembers: 2);
        _files.AddMember("QGPL", "DATA", "SECOND");
        var commands = new CommandService(_system);
        var previous = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.True(commands.Execute("CHGPF FILE(QGPL/DATA) MAXMBRS(1)").IsError);
        Assert.Equal(previous, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Assert.False(commands.Execute("CHGPF FILE(QGPL/DATA) MAXMBRS(3)").IsError);
        _files.AddMember("QGPL", "DATA", "THIRD");
        _system.Security.Authority.Grant("QGPL", "DATA", Ipc.Core.Objects.ObjectType.File, "QUSER", Ipc.Core.Objects.AuthorityBit.ObjectOperate);
        using (Ipc.Services.Events.OperationIdentity.Enter("QUSER"))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ChangePhysicalMemberLimit("QGPL", "DATA", 4)).MessageId);
        Assert.Equal(3, _files.GetDefinition("QGPL", "DATA")!.MaximumMembers);
        Assert.True(commands.Execute("CHGPF FILE(QGPL/DATA) SRCFILE(QGPL/DDS)").IsError);
    }

    [Fact]
    public void Existing_unregistered_table_is_not_adopted_during_member_creation()
    {
        _files.CreatePhysicalFile("QGPL", "DATA", new DdsCompiler().CompilePhysical("DATA", _source), _source);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE \"QGPL.DATA.EXTRA\"(VALUE TEXT); INSERT INTO \"QGPL.DATA.EXTRA\" VALUES('kept')"; command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.AddMember("QGPL", "DATA", "EXTRA"));
        Assert.False(_files.MemberExists("QGPL", "DATA", "EXTRA"));
        command.CommandText = "SELECT VALUE FROM \"QGPL.DATA.EXTRA\""; Assert.Equal("kept", command.ExecuteScalar());
    }

    [Fact]
    public void CRTPF_uses_job_CCSID_and_keeps_PF_LF_descriptors_consistent()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "MemberFixture88!");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); job.Ccsid = 1208;
        _files.CreateSourceFile("QGPL", "DDS"); _files.SaveSourceMember("QGPL", "DDS", "DATA", _source, null);
        var commands = new CommandService(_system, job);
        var created = commands.Execute("CRTPF FILE(QGPL/DATA) SRCFILE(QGPL/DDS)"); Assert.False(created.IsError, created.Message);
        Assert.Equal(1208, _files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Fields[0].Ccsid);
        Assert.Equal(1208, _system.Objects.GetRequired("QGPL", "DATA", "*FILE").Ccsid);
        _files.Insert("QGPL", "DATA", "DATA", "REC", new Dictionary<string, object?> { ["VALUE"] = "€" });
        _files.SaveSourceMember("QGPL", "DDS", "VIEW", LogicalFileTests.Line('R', "VIEWREC", "PFILE(QGPL/DATA)"), null);
        var logical = commands.Execute("CRTLF FILE(QGPL/VIEW) SRCFILE(QGPL/DDS)"); Assert.False(logical.IsError, logical.Message);
        Assert.Equal(1208, _files.GetDefinition("QGPL", "VIEW")!.Ccsid);
        Assert.Equal(1208, _system.Objects.GetRequired("QGPL", "VIEW", "*FILE").Ccsid);
        Assert.Equal("€", Assert.Single(_files.ReadAll("QGPL", "VIEW", "VIEW"))["VALUE"]);
    }
    public void Dispose() => _system.Dispose();
}
