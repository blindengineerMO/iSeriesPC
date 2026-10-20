using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Console.Session;
using static Ipc.Core.Tests.NativePhysicalDdsTests;

namespace Ipc.Core.Tests;

public sealed class InitializeMemberAuthorityTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public InitializeMemberAuthorityTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.Create(new() { Name = "INITUSER" });
        var source = Line('R', "REC") + "\n" + Line(' ', "TEXT", "8", 'A');
        _files.CreatePhysicalFile("QGPL", "DATA", new DdsCompiler().CompilePhysical("DATA", source), source);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "INITUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
    }
    public void Dispose() => _system.Dispose();
    private void Grant(AuthorityBit bits) => _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITUSER", bits);
    [Theory]
    [InlineData(AuthorityBit.ObjectManagement, false)]
    [InlineData(AuthorityBit.ObjectAlter, false)]
    [InlineData(AuthorityBit.ObjectManagement, true)]
    [InlineData(AuthorityBit.ObjectAlter, true)]
    public void Either_management_authority_allows_initialization_without_read_update_or_exist(AuthorityBit management, bool deleted)
    {
        Grant(AuthorityBit.ObjectOperate | management | AuthorityBit.Add | (deleted ? AuthorityBit.Delete : AuthorityBit.None));
        using (OperationIdentity.Enter("INITUSER")) Assert.Equal(2, _files.InitializeMember("QGPL", "DATA", totalRecords: 2, deleted: deleted));
        Assert.Equal(deleted ? 0 : 2, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectOperate)]
    [InlineData(AuthorityBit.Add)]
    [InlineData(AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter)]
    [InlineData(AuthorityBit.Delete)]
    public void Every_required_authority_is_checked_for_deleted_initialization(AuthorityBit missing)
    {
        Grant((AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement | AuthorityBit.ObjectAlter | AuthorityBit.Add | AuthorityBit.Delete) & ~missing);
        using (OperationIdentity.Enter("INITUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 2, deleted: true)).MessageId);
        Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.ObjectAlter)]
    public void CHGPF_accepts_either_management_authority_without_object_operate(AuthorityBit management)
    {
        Grant(management);
        using (OperationIdentity.Enter("INITUSER"))
        {
            var result = new CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) REUSEDLT(*YES) SIZE(2 0 0)"); Assert.False(result.IsError, result.Message);
        }
        Assert.True(_files.GetDefinition("QGPL", "DATA")!.ReuseDeletedRecords);
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.ObjectAlter)]
    public void Add_member_and_clear_accept_either_management_authority(AuthorityBit management)
    {
        Grant(AuthorityBit.ObjectOperate | management | AuthorityBit.Delete);
        using (OperationIdentity.Enter("INITUSER")) { _files.AddMember("QGPL", "DATA", "OTHER"); _files.ClearMember("QGPL", "DATA", "DATA"); }
        Assert.True(_files.MemberExists("QGPL", "DATA", "OTHER"));
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.ObjectAlter)]
    public void CHGLF_accepts_the_same_management_alternatives_without_object_operate(AuthorityBit management)
    {
        var source = LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/DATA)");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source, ["DATA"]);
        _system.Security.Authority.Grant("QGPL", "VIEW", ObjectType.File, "INITUSER", management);
        using (OperationIdentity.Enter("INITUSER"))
        {
            var result = new CommandService(_system).Execute("CHGLF FILE(QGPL/VIEW) WAITRCD(1)"); Assert.False(result.IsError, result.Message);
        }
        Assert.Equal(1, _files.GetDefinition("QGPL", "VIEW")!.RecordLockWaitSeconds);
    }
    [Fact]
    public void Clearing_records_and_adding_members_cannot_bypass_required_management_and_library_add_authority()
    {
        Grant(AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.Delete);
        using (OperationIdentity.Enter("INITUSER"))
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ClearMember("QGPL", "DATA", "DATA")).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.AddMember("QGPL", "DATA", "OTHER")).MessageId);
        }
        Grant(AuthorityBit.ObjectOperate | AuthorityBit.ObjectAlter);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "INITUSER", AuthorityBit.ObjectOperate);
        using (OperationIdentity.Enter("INITUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.AddMember("QGPL", "DATA", "OTHER")).MessageId);
        Assert.False(_files.MemberExists("QGPL", "DATA", "OTHER"));
    }
    [Fact]
    public void Group_authority_supplies_the_alternative_but_an_explicit_private_exclusion_still_denies_it()
    {
        _system.Security.Profiles.Create(new() { Name = "INITGROUP" });
        var profile = _system.Security.Profiles.Get("INITUSER"); profile.GroupProfile = "INITGROUP"; _system.Security.Profiles.Update(profile);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITGROUP", AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.ObjectAlter);
        using (OperationIdentity.Enter("INITUSER")) Assert.Equal(1, _files.InitializeMember("QGPL", "DATA", totalRecords: 1));
        Grant(AuthorityBit.None);
        using (OperationIdentity.Enter("INITUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 2)).MessageId);
        Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Disabled_profiles_and_library_exclusions_are_rechecked_even_on_an_initialization_no_op()
    {
        _files.InitializeMember("QGPL", "DATA", totalRecords: 2, deleted: true);
        Grant(AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.ObjectManagement);
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "INITUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("INITUSER")) Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 1));
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "INITUSER", AuthorityBit.ObjectOperate);
        var profile = _system.Security.Profiles.Get("INITUSER"); profile.Status = Ipc.Core.Security.ProfileStatus.Disabled; _system.Security.Profiles.Update(profile);
        using (OperationIdentity.Enter("INITUSER")) Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 1));
    }
}
