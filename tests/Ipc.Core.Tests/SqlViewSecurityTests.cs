using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewTests
{
    [Fact]
    public void SQL_schema_ownership_requires_profile_ADD_and_the_owners_source_READ()
    {
        Creator(); _system.Security.Profiles.Create(new() { Name = "SCHEMAUSR" }); _system.Libraries.CreateLibrary("SCHEMAUSR");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SCHEMAUSR", AuthorityBit.None);
        _system.Security.Authority.Grant("QSYS", "SCHEMAUSR", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        var options = Options with { Naming = SqlNaming.Sql };
        using (OperationIdentity.Enter("QUSER"))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => _files.ExecuteSql("CREATE VIEW SCHEMAUSR.REPORT AS SELECT ID FROM QGPL.DATA", options)).MessageId);
        _system.Security.Authority.Grant("QSYS", "SCHEMAUSR", ObjectType.UserProfile, "QUSER", AuthorityBit.Add);
        using (OperationIdentity.Enter("QUSER"))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => _files.ExecuteSql("CREATE VIEW SCHEMAUSR.REPORT AS SELECT ID FROM QGPL.DATA", options)).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "SCHEMAUSR", Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER")) _files.ExecuteSql("CREATE VIEW SCHEMAUSR.REPORT AS SELECT ID FROM QGPL.DATA", options);
        var descriptor = _system.Objects.GetRequired("SCHEMAUSR", "REPORT", ObjectType.File); Assert.Equal("SCHEMAUSR", descriptor.Owner); Assert.Equal(AuthorityBit.None, descriptor.PublicAuthority);
        Assert.Equal(3, Query("SELECT ID FROM SCHEMAUSR/REPORT").Count);
    }
    [Fact]
    public void Replacement_preserves_existing_authorization_list_authority_when_library_CRTAUT_changes()
    {
        _system.Objects.Create(new() { Key = new("QSYS", "VWAUTL"), ObjectType = ObjectType.AuthorizationList, Owner = "QSECOFR" });
        var library = _system.Objects.GetRequired("QSYS", "QGPL", ObjectType.Library);
        library.ExtendedAttributes = new Dictionary<string, string>(library.ExtendedAttributes ?? new Dictionary<string, string>()) { ["ipc.library.createAuthority"] = "VWAUTL" }; _system.Objects.Update(library);
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        library.ExtendedAttributes = new Dictionary<string, string>(library.ExtendedAttributes) { ["ipc.library.createAuthority"] = "MISSING" }; _system.Objects.Update(library);
        Run("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA WHERE ID=1");
        Assert.True(_system.Objects.GetRequired("QGPL", "REPORT", ObjectType.File).UseAuthorizationListPublicAuthority);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_authorities WHERE name='REPORT' AND holder='VWAUTL' AND is_authl=1"));
        Assert.Single(Query("SELECT ID FROM QGPL/REPORT"));
    }
    private void Creator()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "SqlViewCreator834");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement);
    }
    [Fact]
    public void Creation_requires_source_READ_single_base_OBJMGT_and_CRTLF_authority()
    {
        Creator(); _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE VIEW REPORT AS SELECT ID FROM DATA")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE VIEW REPORT AS SELECT ID FROM DATA")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement);
        _system.Security.Authority.Grant("QSYS", "CRTLF", ObjectType.Command, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE VIEW REPORT AS SELECT ID FROM DATA")).MessageId);
        Assert.False(_files.FileExists("QGPL", "REPORT"));
    }
    [Fact]
    public void Revoking_the_view_owners_source_READ_invalidates_live_and_cached_EOF_access()
    {
        Creator(); using (OperationIdentity.Enter("QUSER")) Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        using var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/REPORT", Options); Assert.True(query.Read());
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => query.Read()).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement);
        using var complete = _files.OpenSqlQuery("SELECT ID FROM QGPL/REPORT", Options); while (complete.Read()) { }
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement);
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(complete.CheckAccess).MessageId);
    }
    [Fact]
    public void A_disabled_view_owner_is_not_logged_in_to_execute_a_readers_query()
    {
        Creator(); using (OperationIdentity.Enter("QUSER")) Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        var profile = _system.Security.Profiles.Get("QUSER"); profile.Status = ProfileStatus.Disabled; _system.Security.Profiles.Update(profile);
        Assert.Equal(3, Query("SELECT ID FROM QGPL/REPORT").Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_invalidation_hooks_roll_back_view_creation_and_replacement(bool replace)
    {
        if (replace) Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        Scalar("CREATE TRIGGER disable_view_creator AFTER INSERT ON sys_file_members WHEN NEW.name='REPORT' BEGIN UPDATE sys_profiles SET status='Disabled' WHERE name='QSECOFR'; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE " + (replace ? "OR REPLACE " : "") + "VIEW QGPL/REPORT AS SELECT CODE FROM QGPL/DATA")).MessageId);
        Assert.Equal(replace, _files.FileExists("QGPL", "REPORT")); Assert.Equal("Enabled", Scalar("SELECT status FROM sys_profiles WHERE name='QSECOFR'"));
        if (replace) Assert.Equal(3, Query("SELECT ID FROM QGPL/REPORT").Count);
    }
    [Fact]
    public void Cancellation_hooks_roll_back_view_and_job_changes_together()
    {
        Scalar("CREATE TRIGGER cancel_view_creator AFTER INSERT ON sys_file_members WHEN NEW.name='REPORT' BEGIN UPDATE sys_jobs SET cancel_requested=1 WHERE number=" + _session.Job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key)) Assert.ThrowsAny<OperationCanceledException>(() => Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA"));
        Assert.False(_files.FileExists("QGPL", "REPORT")); Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
    }
    [Fact]
    public void DROP_preflights_every_dependent_authority_and_rolls_back_failures_after_child_deletion()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA"); Run("CREATE VIEW QGPL/CHILD AS SELECT ID FROM QGPL/REPORT");
        Creator(); _system.Security.Authority.Grant("QGPL", "REPORT", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectExist);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("DROP VIEW QGPL/REPORT")).MessageId);
        Assert.Equal(3, Query("SELECT ID FROM QGPL/CHILD").Count);
        Scalar("CREATE TRIGGER fail_view_drop BEFORE DELETE ON sys_file_defs WHEN OLD.name='REPORT' BEGIN SELECT RAISE(ABORT,'unavailable'); END");
        Assert.Equal("SQL0901", Assert.Throws<CpfException>(() => Run("DROP VIEW QGPL/REPORT")).MessageId);
        Assert.Equal(3, Query("SELECT ID FROM QGPL/CHILD").Count);
    }
    [Fact]
    public void Open_queries_block_replacement_and_drop_and_cancellation_releases_all_allocations()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key); using var cancel = new CancellationTokenSource();
        using var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/REPORT", Options, cancellationToken: cancel.Token); Assert.True(query.Read());
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA WHERE ID=1")).MessageId);
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("DROP VIEW QGPL/REPORT")).MessageId);
        cancel.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => query.Read());
        Run("DROP VIEW QGPL/REPORT"); Assert.False(_files.FileExists("QGPL", "REPORT"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_waits_are_cancellable_without_publishing_view_changes(bool drop)
    {
        if (drop) Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        using var connection = _system.Connections.Open(); using var transaction = connection.BeginTransaction(deferred: false); using var cancel = new CancellationTokenSource();
        var work = Task.Run(() => Record.Exception(() => _files.ExecuteSql(drop ? "DROP VIEW QGPL/REPORT" : "CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA", Options, cancel.Token)));
        try { await Task.Delay(150); cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { cancel.Cancel(); transaction.Rollback(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(drop, _files.FileExists("QGPL", "REPORT"));
    }
}
