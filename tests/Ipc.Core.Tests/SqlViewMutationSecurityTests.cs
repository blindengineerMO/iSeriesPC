using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewMutationTests
{
    private ExecutionSession Reader(AuthorityBit rights)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "ViewWriterReader321");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "REPORT", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | rights);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.None);
        return new(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
    }
    [Fact]
    public void View_only_write_authority_uses_owner_rights_and_keeps_caller_identity()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<3");
        using var reader = Reader(AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete);
        using (OperationIdentity.Enter("QUSER", reader.Job.Key))
        {
            Assert.Equal(2, Run("UPDATE QGPL/REPORT SET AMOUNT=50"));
            Assert.Equal(1, Run("INSERT INTO QGPL/REPORT VALUES(4,10)"));
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=AMOUNT+1")).MessageId);
            Assert.Equal("QUSER", OperationIdentity.Current!.Principal); Assert.Equal(reader.Job.Key, OperationIdentity.Current.Job);
            Assert.Equal(2, Run("DELETE FROM QGPL/REPORT"));
        }
        Assert.Equal(new long[] { 3, 4 }, Query("SELECT ID FROM QGPL/DATA ORDER BY ID").Select(row => (long)row[0]!));
    }
    [Fact]
    public void Referencing_a_constant_view_column_still_requires_reader_SELECT_authority()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,1 AS N FROM QGPL/DATA");
        using var reader = Reader(AuthorityBit.Update);
        using (OperationIdentity.Enter("QUSER", reader.Job.Key))
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET ID=9 WHERE N=1")).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "REPORT", "REPORT"));
    }
    [Theory]
    [InlineData("UPDATE QGPL/REPORT SET AMOUNT=40")]
    [InlineData("INSERT INTO QGPL/REPORT VALUES(4,10)")]
    [InlineData("DELETE FROM QGPL/REPORT")]
    public void Missing_root_write_authority_cannot_be_replaced_by_view_owner_authority(string sql)
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        using var reader = Reader(AuthorityBit.Read);
        using var identity = OperationIdentity.Enter("QUSER", reader.Job.Key);
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run(sql)).MessageId);
    }
    [Fact]
    public void A_view_owner_without_base_UPDATE_cannot_publish_an_update_even_for_an_administrative_reader()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "ViewOwnerWriter328");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement);
        using (OperationIdentity.Enter("QUSER")) Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=40")).MessageId);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement | AuthorityBit.Update);
        Assert.Equal(3, Run("UPDATE QGPL/REPORT SET AMOUNT=40"));
    }
    [Theory]
    [InlineData("INSERT INTO QGPL/REPORT VALUES(4,10)", "INSERT")]
    [InlineData("UPDATE QGPL/REPORT SET AMOUNT=40", "UPDATE")]
    [InlineData("DELETE FROM QGPL/REPORT", "DELETE")]
    public void Publication_hooks_that_disable_credentials_roll_back_rows_and_profile_changes(string sql, string operation)
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        Scalar("CREATE TRIGGER disable_writer AFTER " + operation + " ON \"QGPL.DATA.DATA\" BEGIN UPDATE sys_profiles SET status='Disabled' WHERE name='QSECOFR'; END");
        using (OperationIdentity.Enter("QSECOFR", _session.Job.Key)) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run(sql)).MessageId);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Equal("Enabled", Scalar("SELECT status FROM sys_profiles WHERE name='QSECOFR'"));
        Assert.Equal("60.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
    [Fact]
    public void A_hook_revoking_view_write_authority_is_checked_on_the_publishers_connection()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA"); using var reader = Reader(AuthorityBit.Update);
        Scalar("CREATE TRIGGER revoke_writer AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN UPDATE sys_authorities SET bits=0 WHERE lib='QGPL' AND name='REPORT' AND holder='QUSER'; END");
        using (OperationIdentity.Enter("QUSER", reader.Job.Key)) Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=40")).MessageId);
        Assert.Equal("60.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
        Assert.True((long)Scalar("SELECT bits FROM sys_authorities WHERE name='REPORT' AND holder='QUSER'")! != 0);
    }
    [Fact]
    public void View_insert_capacity_recovery_uses_delegated_ADD_without_granting_base_access()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA");
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, memberSize: new(InitialRecords: 2, IncrementRecords: 1, MaximumIncrements: 1, Unlimited: false));
        new Ipc.Services.Messages.SystemReplyList(_system.Connections).Put(new(1, "CPA5305", Reply: "I"));
        using var reader = Reader(AuthorityBit.Add);
        new Ipc.Services.Messages.InquiryReplyPolicy(_system.Connections).Set(reader.Job.Key, "*SYSRPYL");
        using (OperationIdentity.Enter("QUSER", reader.Job.Key)) Assert.Equal(1, Run("INSERT INTO QGPL/REPORT VALUES(4,10)"));
        Assert.Equal(4, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(0L, Scalar("SELECT bits FROM sys_authorities WHERE name='DATA' AND holder='QUSER'"));
    }
    [Fact]
    public void Nested_view_owners_need_direct_source_write_and_indirect_source_READ()
    {
        Run("CREATE VIEW QGPL/INNER AS SELECT ID,AMOUNT FROM QGPL/DATA");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "NestedViewOwner329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", Authorities.UseBits | AuthorityBit.Add);
        _system.Security.Authority.Grant("QGPL", "INNER", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.Update);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits | AuthorityBit.ObjectManagement);
        using (OperationIdentity.Enter("QUSER")) Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/INNER");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", Authorities.UseBits);
        Assert.Equal(3, Run("UPDATE QGPL/REPORT SET AMOUNT=40"));
        _system.Security.Authority.Grant("QGPL", "INNER", ObjectType.File, "QUSER", Authorities.UseBits);
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET AMOUNT=50")).MessageId);
        Assert.Equal("120.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
    }
    [Fact]
    public void Delegated_writes_journal_the_real_caller_and_failed_checks_leave_no_entries()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<5 WITH CHECK OPTION");
        _system.Journals.CreateReceiver(new("QGPL", "VWRCV")); _system.Journals.Create(new("QGPL", "VWJRN"), new("QGPL", "VWRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "VWJRN"), "*BOTH");
        using var reader = Reader(AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete | AuthorityBit.Read);
        using (OperationIdentity.Enter("QUSER", reader.Job.Key))
        {
            Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(4,1),(5,2)")).MessageId);
            Assert.Equal(1, Run("INSERT INTO QGPL/REPORT VALUES(4,10)"));
            Assert.Equal(1, Run("UPDATE QGPL/REPORT SET AMOUNT=11 WHERE ID=4"));
            Assert.Equal(1, Run("DELETE FROM QGPL/REPORT WHERE ID=4"));
        }
        var entries = _system.Journals.Read(new("QGPL", "VWJRN"));
        Assert.Equal(new[] { "PT", "UB", "UP", "DL" }, entries.Select(entry => entry.Type));
        Assert.All(entries, entry => { Assert.Equal("QUSER", entry.Principal); Assert.Equal("DATA", entry.ObjectName); });
    }
}
