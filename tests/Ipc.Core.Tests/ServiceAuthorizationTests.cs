using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed class ServiceAuthorizationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public ServiceAuthorizationTests()
    {
        _system.Start();
        _system.Security.Profiles.Create(new UserProfile { Name = "READER" });
        _files = new(_system.Connections, _system.Objects);
        _files.CreateSourceFile("QGPL", "DATA");
        _files.Insert("QGPL", "DATA", "DATA", "DATA", new Dictionary<string, object?> { ["SRCDTA"] = "original" });
    }

    [Fact]
    public void Direct_file_calls_enforce_data_permissions_and_preserve_denied_payloads()
    {
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", Authorities.UseBits);
        using (OperationIdentity.Enter("READER"))
        {
            Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"));
            Assert.Throws<CpfException>(() => _files.Insert("QGPL", "DATA", "DATA", "DATA", new Dictionary<string, object?>()));
            Assert.Throws<CpfException>(() => _files.ClearMember("QGPL", "DATA", "DATA"));
            Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "DATA", "DATA"));
            Assert.Throws<CpfException>(() => _files.DeleteFile("QGPL", "DATA"));
        }
        Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", AuthorityBit.None);
        using (OperationIdentity.Enter("READER")) Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void Object_creation_requires_library_add_and_cannot_assign_an_unapproved_owner()
    {
        using (OperationIdentity.Enter("READER"))
            Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DENIED"), ObjectType = ObjectType.Program, Owner = "READER" }));
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "READER", Authorities.ChangeBits);
        using (OperationIdentity.Enter("READER"))
        {
            _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "OWNED"), ObjectType = ObjectType.Program, Owner = "READER" });
            Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "FORGED"), ObjectType = ObjectType.Program, Owner = "QSECOFR" }));
        }
        Assert.False(_system.Objects.Exists("QGPL", "DENIED", ObjectType.Program));
        Assert.False(_system.Objects.Exists("QGPL", "FORGED", ObjectType.Program));
        Assert.True(_system.Objects.Exists("QGPL", "OWNED", ObjectType.Program));
    }

    [Fact]
    public void Library_exclusion_blocks_an_otherwise_authorized_object()
    {
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "READER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", Authorities.AllBits);
        using (OperationIdentity.Enter("READER")) Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Direct_administrative_calls_cannot_self_grant_or_modify_other_profiles()
    {
        using (OperationIdentity.Enter("READER"))
        {
            Assert.Throws<CpfException>(() => _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "READER", Authorities.AllBits));
            Assert.Throws<CpfException>(() => _system.Security.Authority.AddAuthLMember("NEWLIST", "READER", Authorities.AllBits));
            Assert.Throws<CpfException>(() => _system.Security.Profiles.Get("QSECOFR"));
            var own = _system.Security.Profiles.Get("READER");
            own.SpecialAuthorities = SpecialAuthority.AllObject;
            Assert.Throws<CpfException>(() => _system.Security.Profiles.Update(own));
            Assert.Throws<CpfException>(() => _system.Security.Profiles.Create(new UserProfile { Name = "BACKDOOR" }));
            Assert.Throws<CpfException>(() => _system.SetSystemValue("QSECURITY", "10"));
        }
        Assert.False(_system.Security.Profiles.Exists("BACKDOOR"));
        Assert.False(_system.Objects.Exists("QSYS", "NEWLIST", ObjectType.AuthorizationList));
        Assert.Equal(SpecialAuthority.None, _system.Security.Profiles.Get("READER").SpecialAuthorities);
        Assert.Equal("40", _system.SystemValues.Get("QSECURITY").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_calls_scope_adopted_owner_authority_and_restore_it_on_return(bool rpgParent)
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "OWNER" });
        var file = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File);
        file.Owner = "OWNER";
        file.PublicAuthority = AuthorityBit.None;
        _system.Objects.Update(file);
        var parent = new ObjectDescriptor
        {
            Key = new("QGPL", "PARENT"), ObjectType = ObjectType.Program, Owner = "OWNER",
            Source = rpgParent ? "**free\ncallp CHILD();" : "PGM\nCALL QGPL/CHILD\nENDPGM",
            Attribute = rpgParent ? "RPG" : "CLP", AdoptsOwnerAuthority = true,
        };
        var child = new ObjectDescriptor
        {
            Key = new("QGPL", "CHILD"), ObjectType = ObjectType.Program, Owner = "QSECOFR",
            Source = "PGM\nDSPPFM FILE(QGPL/DATA)\nENDPGM",
        };
        _system.Objects.Create(parent);
        _system.Objects.Create(child);
        var job = _system.Jobs.CreateInteractive("READER");
        var commands = new Ipc.Console.Session.CommandService(_system, job);
        var allowed = commands.Execute("CALL QGPL/PARENT");
        Assert.False(allowed.IsError, allowed.Message);
        Assert.Null(OperationIdentity.Current);
        Assert.True(commands.Execute("DSPPFM FILE(QGPL/DATA)").IsError);
        child.UsesAdoptedAuthority = false;
        _system.Objects.Update(child);
        Assert.True(commands.Execute("CALL QGPL/PARENT").IsError);
        Assert.Null(OperationIdentity.Current);
    }

    [Fact]
    public void Program_adoption_does_not_inherit_the_owners_group_authority()
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "POWER" });
        _system.Security.Profiles.Create(new UserProfile { Name = "OWNER", GroupProfile = "POWER" });
        _system.Security.Authority.SetPublicAuthority("QGPL", "DATA", ObjectType.File, AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "POWER", Authorities.AllBits);
        _system.Objects.Create(new ObjectDescriptor
        {
            Key = new("QGPL", "ADOPTER"), ObjectType = ObjectType.Program, Owner = "OWNER", AdoptsOwnerAuthority = true,
            Source = "PGM\nDSPPFM FILE(QGPL/DATA)\nENDPGM",
        });
        var job = _system.Jobs.CreateInteractive("READER");
        Assert.True(new Ipc.Console.Session.CommandService(_system, job).Execute("CALL QGPL/ADOPTER").IsError);
        Assert.Null(OperationIdentity.Current);
    }

    [Fact]
    public void Object_management_alone_cannot_enable_adoption_of_another_profile()
    {
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "PROGRAM"), ObjectType = ObjectType.Program, Owner = "QSECOFR", Source = "PGM\nENDPGM" });
        _system.Security.Authority.Grant("QGPL", "PROGRAM", ObjectType.Program, "READER", Authorities.AllBits);
        using (OperationIdentity.Enter("READER"))
        {
            var descriptor = _system.Objects.GetRequired("QGPL", "PROGRAM", ObjectType.Program);
            descriptor.AdoptsOwnerAuthority = true;
            Assert.Throws<CpfException>(() => _system.Objects.Update(descriptor));
        }
        Assert.False(_system.Objects.GetRequired("QGPL", "PROGRAM", ObjectType.Program).AdoptsOwnerAuthority);
    }

    [Fact]
    public void Other_jobs_and_audit_cursors_are_not_accessible_without_special_authority()
    {
        var own = _system.Jobs.CreateInteractive("READER");
        var other = _system.Jobs.CreateInteractive("QUSER");
        using (OperationIdentity.Enter("READER", own.Key))
        {
            Assert.NotEmpty(_system.Jobs.GetLog(own.Key));
            Assert.Throws<CpfException>(() => _system.Jobs.GetLog(other.Key));
            Assert.Throws<CpfException>(() => _system.Jobs.Complete(other.Key));
            Assert.DoesNotContain(_system.Jobs.List(), job => job.Key == other.Key);
            Assert.Throws<CpfException>(() => _system.DurableEvents.Read("steal-audit"));
            Assert.Throws<CpfException>(() => _system.Log.Recent(10));
            Assert.Throws<CpfException>(() => _system.Jobs.Submit("FAKE", "", "QBATCH", profile: "QSECOFR"));
            Assert.Throws<CpfException>(() => _system.Subsystems.End("QBATCH"));
            Assert.Throws<CpfException>(() => _system.DurableEvents.Append("security.fake", new { }, principal: "QSECOFR"));
        }
        Assert.Equal(Ipc.Core.Work.JobStatus.Active, _system.Jobs.GetRequired(other.Key).Status);
        Assert.True(_system.Subsystems.IsActive("QBATCH"));
    }

    [Fact]
    public void Returned_configuration_and_system_values_cannot_bypass_authorized_setters()
    {
        using (OperationIdentity.Enter("READER"))
        {
            _system.SystemValues.Get("QSECURITY").Value = "10";
            _system.SystemValues.Get("QPWDMINLEN").Value = "1";
            _system.Config.SystemName = "FORGED";
            Assert.Equal("40", _system.SystemValues.Get("QSECURITY").Value);
            Assert.Equal("6", _system.SystemValues.Get("QPWDMINLEN").Value);
            Assert.NotEqual("FORGED", _system.Config.SystemName);
        }
    }

    [Fact]
    public void Native_host_source_import_requires_service_authority()
    {
        var job = _system.Jobs.CreateInteractive("READER");
        var result = new Ipc.Console.Session.CommandService(_system, job).Execute("CRTCLPGM LIB(QGPL) PGM(IMPORT) SRCSTMF('/etc/passwd')");
        Assert.True(result.IsError);
        Assert.Contains("CPF9802", result.Message);
        Assert.False(_system.Objects.Exists("QGPL", "IMPORT", ObjectType.Program));
    }

    [Fact]
    public void Disabling_a_live_profile_denies_work_but_does_not_prevent_server_job_cleanup()
    {
        var profile = _system.Security.Profiles.Get("READER");
        using var controller = new Ipc.Console.Session.MenuController(_system, profile);
        profile.Status = ProfileStatus.Disabled;
        _system.Security.Profiles.Update(profile);
        using (OperationIdentity.Enter("READER", controller.JobKey))
            Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "DATA", "DATA"));
        controller.EndSession(Ipc.Core.Work.JobCompletion.Abnormal, "Profile disabled.");
        Assert.Equal(Ipc.Core.Work.JobStatus.Completed, _system.Jobs.GetRequired(controller.JobKey).Status);
        Assert.Contains(_system.Jobs.GetLog(controller.JobKey), message => message.MessageType == "COMPLETION");
    }

    public void Dispose() => _system.Dispose();
}
