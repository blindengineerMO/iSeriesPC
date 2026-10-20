using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests
{
    private ExecutionSession AlterUser(AuthorityBit rights)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "AlterTableUser329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", rights);
        return new(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectAlter, true)]
    [InlineData(AuthorityBit.ObjectManagement, false)]
    [InlineData(AuthorityBit.ObjectExist, false)]
    [InlineData(AuthorityBit.Update, false)]
    public void ALTER_requires_OBJALTER_and_does_not_require_caller_READ_or_ADD(AuthorityBit rights, bool accepted)
    {
        using var session = AlterUser(rights); using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            if (accepted) Run("ALTER TABLE QGPL/DATA ADD N INTEGER");
            else Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId);
        }
        Assert.Equal(accepted, _files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("N") is not null);
    }
    [Fact]
    public void Publisher_connection_rechecks_revoked_ALTER_after_definition_hooks_and_rolls_back_everything()
    {
        using var session = AlterUser(AuthorityBit.ObjectAlter); var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Scalar("CREATE TRIGGER revoke_alter AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='DATA' BEGIN UPDATE sys_authorities SET bits=0 WHERE name='DATA' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Assert.Equal((long)AuthorityBit.ObjectAlter, Scalar("SELECT bits FROM sys_authorities WHERE name='DATA' AND holder='QUSER'"));
    }
    [Fact]
    public void Owner_authority_and_existing_private_public_attributes_survive_ALTER()
    {
        using var session = AlterUser(AuthorityBit.ObjectAlter);
        var before = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File);
        using (OperationIdentity.Enter("QUSER", session.Job.Key)) Run("ALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE VARCHAR(5)");
        var after = _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File);
        Assert.Equal(before.Owner, after.Owner); Assert.Equal(before.PublicAuthority, after.PublicAuthority); Assert.Equal(before.Created, after.Created);
        Assert.Equal((long)AuthorityBit.ObjectAlter, Scalar("SELECT bits FROM sys_authorities WHERE name='DATA' AND holder='QUSER'"));
    }
    [Fact]
    public void Enlisted_base_members_reject_ALTER_without_changing_pending_record_data()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "AlterTableAdmin327");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        _system.Journals.CreateReceiver(new("QGPL", "ALTRCV")); _system.Journals.Create(new("QGPL", "ALTJRN"), new("QGPL", "ALTRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "ALTJRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        _system.RecordTransactions.Begin(session.Job.Key); _system.RecordTransactions.Enlist(session.Job.Key, new("QGPL", "DATA"));
        try { Assert.Equal("IPC0211", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId); }
        finally { _system.RecordTransactions.Rollback(session.Job.Key); _system.RecordTransactions.End(session.Job.Key); }
        Assert.Null(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("N"));
    }
    [Fact]
    public void An_open_dependent_view_allocation_blocks_ALTER_and_preserves_the_cursor()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID FROM QGPL/DATA");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "AlterTableAdmin327");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using var cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL/REPORT ORDER BY ID", Options); Assert.True(cursor.Read());
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId);
        Assert.True(cursor.Read()); Assert.Equal(2L, cursor.Current![0]);
    }
}
