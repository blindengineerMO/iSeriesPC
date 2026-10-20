using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Fact]
    public void Pending_constraints_allow_metadata_and_allocation_inspection_while_data_IO_is_blocked()
    {
        StatePair(); State("CHILD", false); Run("INSERT INTO QGPL/CHILD VALUES(2)"); Assert.True(State("CHILD", true).CheckPending);
        Assert.True(_files.FileExists("QGPL", "CHILD")); Assert.NotNull(_files.GetDefinition("QGPL", "CHILD"));
        Assert.Empty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL", "CHILD", ObjectType.File)));
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "CHILD", "CHILD")).MessageId);
        using var session = NativeForeignSession(); var repaired = session.Execute("CHGPFCST CHILD *chkpnd STATE(*DISABLED)");
        Assert.False(repaired.IsError, repaired.Message); Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }
    [Fact]
    public void Failed_member_rewrite_restores_CHECK_enforcement_and_the_original_definition()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))");
        Scalar("INSERT INTO \"QGPL.DATA.DATA\" VALUES(2147483648)");
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson(); var failed = State("DATA", false, "POS");
        Assert.NotNull(failed.Error); Assert.False(failed.Changed); Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Scalar("UPDATE \"QGPL.DATA.DATA\" SET ID=1");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(-1)")).MessageId);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_alter_*'"));
    }
    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.ObjectAlter)]
    public void Administration_delegates_parent_validation_without_parent_data_or_child_operate_rights(AuthorityBit authority)
    {
        StatePair();
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "StateUserPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        foreach (var name in new[] { "PARENT", "CHILD" })
        { var descriptor = _system.Objects.GetRequired("QGPL", name, ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor); }
        _system.Security.Authority.Grant("QGPL", "CHILD", ObjectType.File, "QUSER", authority);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using var identity = OperationIdentity.Enter("QUSER", session.Job.Key);
        Assert.Null(State("CHILD", false).Error);
        var changed = session.Execute("CHGPFCST CHILD REF STATE(*ENABLED)"); Assert.False(changed.IsError, changed.Message);
        Assert.Null(State("CHILD", true).Error);
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "PARENT", "PARENT")).MessageId);
    }

    [Fact]
    public void Child_data_rights_do_not_grant_constraint_administration()
    {
        StatePair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "StateDeniedPassword329");
        var descriptor = _system.Objects.GetRequired("QGPL", "CHILD", ObjectType.File); descriptor.PublicAuthority = Authorities.ChangeBits; _system.Objects.Update(descriptor);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using var identity = OperationIdentity.Enter("QUSER", session.Job.Key);
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => State("CHILD", false)).MessageId);
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Disabled);
    }

    [Theory]
    [InlineData("PARENT")]
    [InlineData("CHILD")]
    public void Retained_update_opens_block_constraint_changes_and_leave_no_leaked_leases(string file)
    {
        StatePair(); using var session = NativeForeignSession();
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var cursor = _files.OpenUpdateCursor("QGPL", file, file))
        {
            Assert.NotNull(cursor.Read()); var result = State("CHILD", false);
            Assert.NotNull(result.Error); Assert.Equal("CPF1002", result.Error.MessageId); Assert.False(result.Changed);
            Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Disabled);
        }
        Assert.Null(State("CHILD", false).Error); Assert.Null(State("CHILD", true).Error);
        using var locks = _system.Connections.Locks.Open(); using var read = locks.CreateCommand();
        read.CommandText = "SELECT count(*) FROM holders WHERE lifetime='Referential'"; Assert.Equal(0L, read.ExecuteScalar());
    }

    [Theory]
    [InlineData("PARENT")]
    [InlineData("CHILD")]
    public void Retained_queries_block_constraint_changes_until_closed(string file)
    {
        StatePair(); using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/" + file, Options))
        {
            Assert.True(query.Read()); var result = State("CHILD", false); Assert.NotNull(result.Error);
            Assert.Equal("CPF1002", result.Error.MessageId); Assert.False(result.Changed);
        }
        Assert.Null(State("CHILD", false).Error);
    }

    [Fact]
    public void CLRPFM_allows_clearing_a_disabled_parent_without_changing_its_child()
    {
        StatePair(); Assert.Null(State("CHILD", false).Error); using var session = NativeForeignSession();
        var result = session.Execute("CLRPFM FILE(QGPL/PARENT)"); Assert.False(result.IsError, result.Message);
        Assert.Equal(0, _files.RowCount("QGPL", "PARENT", "PARENT")); Assert.Equal(1, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }

    [Fact]
    public void State_changes_recheck_management_authority_after_publication_hooks()
    {
        StatePair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "StateRevokePassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        var descriptor = _system.Objects.GetRequired("QGPL", "CHILD", ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor);
        _system.Security.Authority.Grant("QGPL", "CHILD", ObjectType.File, "QUSER", AuthorityBit.ObjectManagement);
        var before = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        Scalar("CREATE TRIGGER revoke_constraint_admin AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='CHILD' BEGIN UPDATE sys_authorities SET bits=0 WHERE name='CHILD' AND holder='QUSER'; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        { var result = State("CHILD", false); Assert.NotNull(result.Error); Assert.Equal("CPF9802", result.Error.MessageId); }
        Assert.Equal(before, _files.GetDefinition("QGPL", "CHILD")!.ToJson());
        Assert.Equal((long)AuthorityBit.ObjectManagement, Scalar("SELECT bits FROM sys_authorities WHERE name='CHILD' AND holder='QUSER'"));
    }

    [Theory]
    [InlineData("UPDATE sys_file_members SET mbr='CHANGED' WHERE name='PARENT'")]
    [InlineData("UPDATE sys_record_slots SET id='changed-parent-slot' WHERE name='PARENT'")]
    [InlineData("UPDATE sys_record_slots SET id='changed-child-slot' WHERE name='CHILD'")]
    [InlineData("UPDATE sys_objects SET owner='QUSER' WHERE name='CHILD' AND type='*FILE'")]
    public void Publication_hooks_cannot_change_bound_members_or_ownership(string mutation)
    {
        StatePair(); var before = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        var slot = Scalar("SELECT id FROM sys_record_slots WHERE name='PARENT'");
        Scalar("CREATE TRIGGER change_constraint_binding AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='CHILD' BEGIN " + mutation + "; END");
        var result = State("CHILD", false); Assert.NotNull(result.Error);
        Assert.Equal(before, _files.GetDefinition("QGPL", "CHILD")!.ToJson()); Assert.Equal("PARENT", _files.FirstMember("QGPL", "PARENT"));
        Assert.Equal(slot, Scalar("SELECT id FROM sys_record_slots WHERE name='PARENT'"));
        Assert.Equal("QSECOFR", _system.Objects.GetRequired("QGPL", "CHILD", ObjectType.File).Owner);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
    }

    [Fact]
    public void Pending_state_restrictions_follow_view_and_logical_sources()
    {
        StatePair(); Run("CREATE VIEW QGPL/CHVIEW AS SELECT ID FROM QGPL/CHILD");
        var source = LogicalFileTests.Line('R', "CHREC", "PFILE(QGPL/CHILD)") + "\n" + LogicalFileTests.Line('K', "ID");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "CHILD"), _files.GetDefinition("QGPL", "CHILD")!)).Compile("LOGICAL", source);
        _files.CreateLogicalFile("QGPL", "LOGICAL", definition, source);
        State("CHILD", false); Run("INSERT INTO QGPL/CHILD VALUES(2)"); Assert.True(State("CHILD", true).CheckPending);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.OpenSqlQuery("SELECT ID FROM QGPL/CHVIEW", Options)).MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "LOGICAL", "LOGICAL")).MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHVIEW VALUES(1)")).MessageId);
    }

    [Fact]
    public void Disabled_partial_SET_NULL_hooks_stay_inactive_after_copy_and_reenable()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,CONSTRAINT REF FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); Assert.Null(State("CHILD", false).Error);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_sql_fk_null_*'"));
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(2L, Scalar("SELECT B FROM \"QGPL.COPIED.CHILD\""));
        Run("INSERT INTO QGPL/PARENT VALUES(1,2)"); Assert.Null(State("CHILD", true).Error);
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(DBNull.Value, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal(2L, Scalar("SELECT B FROM \"QGPL.COPIED.CHILD\""));
    }

    [Fact]
    public void Copied_disabled_CHECKs_preserve_invalid_rows_and_keep_independent_state()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))"); State("DATA", false, "POS"); Run("INSERT INTO QGPL/DATA VALUES(-1)");
        _system.ObjectOperations.Relocate(new("QGPL", "DATA"), ObjectType.File, new("QGPL", "COPIED"), copy: true);
        var copied = _files.GetDefinition("QGPL", "COPIED")!.SqlTable!.Checks![0]; Assert.True(copied.Disabled); Assert.NotEqual("POS", copied.Name);
        Assert.True(State("COPIED", true, copied.Name!).CheckPending); Assert.Equal(-1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.False(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks![0].CheckPending);
    }

    [Fact]
    public void Copy_with_missing_parent_creates_a_defined_constraint_that_can_later_be_established()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "COPIED"), copy: true);
        var foreign = _files.GetDefinition("DEST", "COPIED")!.SqlTable!.ForeignKeys![0]; Assert.True(foreign.Defined); Assert.Null(foreign.ParentMember);
        Assert.Equal("DEST", foreign.Parent.Schema); Run("INSERT INTO DEST/COPIED VALUES(2)");
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO DEST/PARENT VALUES(1),(2)");
        var changed = Assert.Single(_files.ChangeConstraints("DEST", "COPIED", [foreign.Name!], enabled: true));
        Assert.Null(changed.Error); Assert.False(changed.Defined); Assert.False(changed.CheckPending);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/COPIED VALUES(3)")).MessageId);
    }

    [Fact]
    public void Copied_data_mismatches_publish_pending_state_and_repair_without_changing_the_source()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST"); Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "COPIED"), copy: true);
        var foreign = _files.GetDefinition("DEST", "COPIED")!.SqlTable!.ForeignKeys![0]; Assert.True(foreign.CheckPending); Assert.False(foreign.Defined);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("DEST", "COPIED", "CHILD")).MessageId);
        Assert.Equal(1, _files.RowCount("QGPL", "CHILD", "CHILD")); Run("INSERT INTO DEST/PARENT VALUES(1)");
        var result = Assert.Single(_files.ChangeConstraints("DEST", "COPIED", ["*CHKPND"], enabled: true)); Assert.Null(result.Error);
        Assert.Equal(1, _files.RowCount("DEST", "COPIED", "CHILD"));
    }
}
