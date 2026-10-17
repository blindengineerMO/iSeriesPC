using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class UserSpaceDeletionTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private UserSpaceStore Store => new(_system.Connections);
    public UserSpaceDeletionTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "UserSpaceFixture25");
    }
    private int Delete(string name, string library = "QGPL") => Store.Delete(name, library, l => _system.SearchLibraries(null, l));
    private void Create(string library, params string[] names)
    {
        if (!_system.Libraries.LibraryExists(library)) _system.Libraries.CreateLibrary(library);
        foreach (var name in names) Store.Create(new(library, name), 2, 0xFE);
    }
    private bool Exists(string library, string name) => _system.Objects.Exists(library, name, ObjectType.UserSpace);

    [Fact]
    public void Generic_prefixes_treat_underscores_literally_and_do_not_delete_other_types()
    {
        Create("QGPL", "A_", "A_ONE", "ABONE", "B");
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "A_ONE"), ObjectType = ObjectType.Program });
        Assert.Equal(2, Delete("a_*")); Assert.True(Exists("QGPL", "ABONE")); Assert.True(Exists("QGPL", "B"));
        Assert.True(_system.Objects.Exists("QGPL", "A_ONE", ObjectType.Program));
        Assert.Equal("CPF2125", Assert.Throws<CpfException>(() => Delete("A_*")).MessageId);
        Assert.Equal("CPF2105", Assert.Throws<CpfException>(() => Delete("MISSING")).MessageId);
        Assert.Equal("CPF2110", Assert.Throws<CpfException>(() => Delete("B", "MISSING")).MessageId);
    }
    [Theory]
    [InlineData("*")]
    [InlineData("*ALL")]
    [InlineData("A*B")]
    [InlineData("A**")]
    [InlineData("ABCDEFGHIJ*")]
    [InlineData("")]
    public void Invalid_patterns_preserve_all_objects(string name)
    {
        Create("QGPL", "ABC"); Assert.Throws<CpfException>(() => Delete(name)); Assert.True(Exists("QGPL", "ABC"));
    }
    [Fact]
    public void Libl_stops_at_first_matching_library_even_when_its_objects_are_denied()
    {
        Create("FIRST", "MATCH1", "MATCH2"); Create("SECOND", "MATCH1");
        foreach (var name in new[] { "MATCH1", "MATCH2" })
            _system.Security.Authority.Grant("FIRST", name, ObjectType.UserSpace, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER"))
        {
            Assert.Equal("CPF2125", Assert.Throws<CpfException>(() => Store.Delete("MATCH*", "*LIBL", _ => ["FIRST", "SECOND"])).MessageId);
        }
        Assert.True(Exists("SECOND", "MATCH1"));
        Assert.Equal(2, Store.Delete("MATCH*", "*LIBL", _ => ["FIRST", "SECOND"]));
        Assert.True(Exists("SECOND", "MATCH1"));
    }
    [Fact]
    public void Usrlibl_includes_configured_current_and_user_libraries_and_excludes_system_portion()
    {
        Create("CURRENT", "MATCH"); Create("USERLIB", "MATCH"); Create("SYSTEMLIB", "MATCH");
        _system.SetSystemValue(SystemValueNames.SystemLibraryList, "QSYS,SYSTEMLIB");
        var job = _system.Jobs.CreateInteractive("QSECOFR"); job.CurrentLibrary = "CURRENT"; job.LibraryList = "USERLIB CURRENT";
        Assert.Equal(new[] { "CURRENT", "USERLIB" }, _system.SearchLibraries(job, "*USRLIBL"));
        using var session = new ExecutionSession(_system, job, CancellationToken.None);
        var result = session.Execute("DLTUSRSPC *USRLIBL/MATCH"); Assert.False(result.IsError, result.Message);
        Assert.False(Exists("CURRENT", "MATCH")); Assert.False(Exists("USERLIB", "MATCH")); Assert.True(Exists("SYSTEMLIB", "MATCH"));
        job.CurrentLibrary = "*CRTDFT";
        Assert.Equal(new[] { "USERLIB", "CURRENT" }, _system.SearchLibraries(job, "*USRLIBL"));
    }
    [Fact]
    public void Allusr_obeys_system_library_exclusions_and_documented_user_data_libraries()
    {
        string[] included = ["APP", "QGPL", "QUSRSYS", "QSYS2", "QRCL00001", "QSYS200001", "QUSRV7R4M0", "#APP"];
        string[] excluded = ["QSYS", "QOTHER", "QRCLABCDE", "QSYS2ABCDE", "QUSRVXR4M0", "#CGULIB", "#COBLIB", "#DFULIB", "#DSULIB", "#RPGLIB", "#SDALIB", "#SEULIB"];
        foreach (var library in included.Concat(excluded)) Create(library, "DELETE1");
        Assert.Equal(included.Length, Delete("DELETE*", "*ALLUSR"));
        foreach (var library in included) Assert.False(Exists(library, "DELETE1"));
        foreach (var library in excluded) Assert.True(Exists(library, "DELETE1"));
    }
    [Fact]
    public void All_searches_own_qtemp_and_never_touches_foreign_jobs_even_as_allobj()
    {
        Create("QGPL", "DELETE1"); Create("QSYS", "DELETE1");
        var own = _system.Jobs.CreateInteractive("QSECOFR"); var other = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", own.Key)) Store.Create(new("QTEMP", "DELETE1"), 2);
        using (OperationIdentity.Enter("QSECOFR", other.Key)) Store.Create(new("QTEMP", "DELETE1"), 2);
        using (OperationIdentity.Enter("QSECOFR", own.Key))
        {
            Assert.Equal(1, Delete("DELETE*", "*ALLUSR"));
            Assert.Equal(2, Delete("DELETE*", "*ALL"));
            Assert.Throws<CpfException>(() => Store.Inspect(new("QTEMP", "DELETE1")));
        }
        using (OperationIdentity.Enter("QSECOFR", other.Key)) Assert.Equal(2, Store.Inspect(new("QTEMP", "DELETE1")).Size);
    }
    [Fact]
    public void Partial_deletion_commits_authorized_objects_and_reports_denied_and_protected_matches()
    {
        Create("QGPL", "MATCHA", "MATCHB", "MATCHC");
        _system.Security.Authority.Grant("QGPL", "MATCHA", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        _system.Security.Authority.Grant("QGPL", "MATCHB", ObjectType.UserSpace, "QUSER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "MATCHC", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DEPENDENT"), ObjectType = ObjectType.Program });
        new ObjectCatalogOperations(_system.Connections).AddDependency(new("QGPL", "DEPENDENT"), ObjectType.Program, new("QGPL", "MATCHC"), ObjectType.UserSpace);
        using (OperationIdentity.Enter("QUSER"))
        {
            var failure = Assert.Throws<CpfException>(() => Delete("MATCH*")); Assert.Equal("CPF2117", failure.MessageId);
            Assert.Contains("1 user spaces deleted; 2 matched objects not deleted", failure.Message);
            Assert.Equal("CPF2125", Assert.Throws<CpfException>(() => Delete("MATCH*")).MessageId);
            Assert.Equal("CPF2189", Assert.Throws<CpfException>(() => Delete("MATCHB")).MessageId);
        }
        Assert.False(Exists("QGPL", "MATCHA")); Assert.True(Exists("QGPL", "MATCHB"));
        Assert.Equal("FEFE", Convert.ToHexString(Store.Read(new("QGPL", "MATCHC"), 1, 2)));
    }
    [Fact]
    public void Allocated_matches_survive_while_unallocated_matches_are_deleted()
    {
        Create("QGPL", "MATCHA", "MATCHB"); var holder = _system.Jobs.CreateInteractive("QSECOFR");
        using var held = new JobLockStore(_system.Connections).Acquire(holder.Key, new("QGPL", "MATCHB", ObjectType.UserSpace), JobLockMode.SharedRead, TimeSpan.Zero);
        using var execution = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        var result = execution.Execute("DLTUSRSPC QGPL/MATCH*"); Assert.True(result.IsError); Assert.Contains("CPF2117", result.Message);
        Assert.False(Exists("QGPL", "MATCHA")); Assert.True(Exists("QGPL", "MATCHB"));
        result = execution.Execute("DLTUSRSPC QGPL/MATCHB"); Assert.True(result.IsError); Assert.Contains("CPF2114", result.Message);
    }
    [Fact]
    public void Broad_search_skips_inaccessible_libraries_without_deleting_their_objects()
    {
        Create("DENIED", "MATCH"); Create("QGPL", "MATCH");
        _system.Security.Authority.Grant("QSYS", "DENIED", ObjectType.Library, "QUSER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "MATCH", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        using (OperationIdentity.Enter("QUSER"))
        {
            Assert.Equal("CPF2182", Assert.Throws<CpfException>(() => Delete("MATCH", "DENIED")).MessageId);
            Assert.Equal("CPF2117", Assert.Throws<CpfException>(() => Delete("MATCH", "*ALLUSR")).MessageId);
        }
        Assert.True(Exists("DENIED", "MATCH")); Assert.False(Exists("QGPL", "MATCH"));
    }
    [Fact]
    public void Unexpected_database_failure_rolls_back_prior_deletions_and_authorities()
    {
        Create("QGPL", "MATCHA", "MATCHB");
        _system.Security.Authority.Grant("QGPL", "MATCHA", ObjectType.UserSpace, "QUSER", AuthorityBit.ObjectExist);
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_space_delete BEFORE DELETE ON sys_objects WHEN OLD.name='MATCHB' AND OLD.type='*USRSPC' BEGIN SELECT RAISE(ABORT,'injected deletion failure'); END";
        command.ExecuteNonQuery(); Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Delete("MATCH*"));
        Assert.True(Exists("QGPL", "MATCHA")); Assert.True(Exists("QGPL", "MATCHB"));
        Assert.Equal("FEFE", Convert.ToHexString(Store.Read(new("QGPL", "MATCHA"), 1, 2)));
        command.CommandText = "DROP TRIGGER fail_space_delete"; command.ExecuteNonQuery();
        using (OperationIdentity.Enter("QUSER")) Assert.Equal(1, Delete("MATCHA"));
    }
    [Theory]
    [InlineData(SpecialAuthority.SecurityAdministrator)]
    [InlineData(SpecialAuthority.AllObject)]
    public void Domain_policy_requires_both_security_and_all_object_authority(SpecialAuthority authority)
    {
        _system.Security.Profiles.Create(new UserProfile { Name = "POLICY", SpecialAuthorities = authority });
        using (OperationIdentity.Enter("POLICY"))
            Assert.Throws<CpfException>(() => _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP"));
        Assert.Equal("*ALL", _system.SystemValues.Get(SystemValueNames.AllowUserDomain).Value);
    }
    public void Dispose() => _system.Dispose();
}
