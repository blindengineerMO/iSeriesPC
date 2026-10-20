using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private void RelocationDefinedPair()
    {
        StatePair("CASCADE"); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "CHILD"), copy: true);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData("MOVE")]
    [InlineData("RENAME")]
    [InlineData("COPY")]
    public void Relocating_a_parent_establishes_defined_destination_relationships(string operation)
    {
        RelocationDefinedPair();
        if (operation == "RENAME")
        { Run("CREATE TABLE DEST/OTHER(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO DEST/OTHER VALUES(1)"); }
        _system.ObjectOperations.Relocate(operation == "RENAME" ? new("DEST", "OTHER") : new("QGPL", "PARENT"),
            ObjectType.File, new("DEST", "PARENT"), copy: operation == "COPY");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.False(foreign.CheckPending);
        Assert.Equal(operation == "RENAME" ? "OTHER" : "PARENT", foreign.ParentMember);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
        Run("DELETE FROM DEST/PARENT");
        Assert.Equal(0, _files.RowCount("DEST", "CHILD", "CHILD"));
    }

    [Fact]
    public void Duplicating_a_parent_with_unmatched_child_rows_establishes_a_pending_relationship()
    {
        RelocationDefinedPair(); Run("INSERT INTO DEST/CHILD VALUES(2)");
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.CheckPending);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(3)")).MessageId);
    }

    [Fact]
    public void Parent_duplication_establishes_disabled_defined_relationships_without_enabling_them()
    {
        RelocationDefinedPair(); Assert.Null(_files.ChangeConstraints("DEST", "CHILD", ["*ALL"], false)[0].Error);
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.Disabled); Assert.True(foreign.CheckPending);
        Run("INSERT INTO DEST/CHILD VALUES(2)");
    }

    [Fact]
    public void Renaming_a_library_establishes_defined_references_to_its_new_library_name()
    {
        RelocationDefinedPair();
        _system.Libraries.CreateLibrary("HOLDLIB");
        _system.ObjectOperations.Relocate(new("DEST", "CHILD"), ObjectType.File, new("HOLDLIB", "PENDING"));
        _system.ObjectOperations.Delete(new("QSYS", "DEST"), ObjectType.Library);
        _system.Libraries.CreateLibrary("OLDLIB");
        Run("CREATE TABLE OLDLIB/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO OLDLIB/PARENT VALUES(1)");
        _system.ObjectOperations.Relocate(new("QSYS", "OLDLIB"), ObjectType.Library, new("QSYS", "DEST"));
        Assert.False(_files.GetDefinition("HOLDLIB", "PENDING")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO HOLDLIB/PENDING VALUES(2)")).MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relocation_establishment_preflights_retained_child_and_index_queries(bool index)
    {
        RelocationDefinedPair(); if (index) Run("CREATE INDEX DEST/CHIDX ON DEST/CHILD(ID)");
        using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var query = _files.OpenSqlQuery("SELECT ID FROM DEST/" + (index ? "CHIDX" : "CHILD"), Options))
        {
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
                new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
            Assert.False(_files.FileExists("DEST", "PARENT"));
        }
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parent_relocation_delegates_establishment_without_child_data_or_management_rights(bool copy)
    {
        RelocationDefinedPair();
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "RelocationDelegatePassword329");
        foreach (var library in new[] { "QGPL", "DEST" })
            _system.Security.Authority.Grant("QSYS", library, ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        foreach (var key in new[] { ("QGPL", "PARENT"), ("QGPL", "CHILD"), ("DEST", "CHILD") })
        { var descriptor = _system.Objects.GetRequired(key.Item1, key.Item2, ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor); }
        _system.Security.Authority.Grant("QGPL", "PARENT", ObjectType.File, "QUSER",
            copy ? AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.ObjectManagement : AuthorityBit.ObjectManagement);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: copy);
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
    }

    [Theory]
    [InlineData("UPDATE sys_objects SET owner='QUSER' WHERE lib='DEST' AND name='PARENT' AND type='*FILE'")]
    [InlineData("UPDATE sys_record_slots SET id='changed' WHERE lib='DEST' AND name='CHILD'")]
    [InlineData("UPDATE sys_file_members SET mbr='CHANGED' WHERE lib='DEST' AND name='CHILD'")]
    [InlineData("UPDATE sys_file_defs SET def=json_set(def,'$.maximumMembers',2) WHERE lib='DEST' AND name='PARENT'")]
    public void Establishing_a_relocated_parent_rolls_back_late_related_identity_changes(string mutation)
    {
        RelocationDefinedPair(); var before = _files.GetDefinition("DEST", "CHILD")!.ToJson();
        using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        Scalar("CREATE TRIGGER change_establishment AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN " + mutation + "; END");
        Assert.Equal("CPF32B2", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
            new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.Equal(before, _files.GetDefinition("DEST", "CHILD")!.ToJson());
        Assert.Equal(1, _files.RowCount("DEST", "CHILD", "CHILD"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_foreign_*'"));
        using var locks = _system.Connections.Locks.Open(); using var held = locks.CreateCommand();
        held.CommandText = "SELECT count(*) FROM holders WHERE lifetime='Referential'"; Assert.Equal(0L, held.ExecuteScalar());
    }

    [Fact]
    public void Parent_copy_publication_cannot_change_a_defined_child_before_establishment_planning()
    {
        RelocationDefinedPair(); var before = _files.GetDefinition("DEST", "CHILD")!.ToJson();
        Scalar("CREATE TRIGGER change_before_establishment AFTER INSERT ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='PARENT' BEGIN UPDATE sys_file_defs SET def=json_set(def,'$.maximumMembers',2) WHERE lib='DEST' AND name='CHILD'; END");
        Assert.Equal("CPF32B2", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
            new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.Equal(before, _files.GetDefinition("DEST", "CHILD")!.ToJson());
    }

    [Fact]
    public void Establishment_preserves_surviving_indexes_views_versions_and_application_and_journal_hooks()
    {
        RelocationDefinedPair(); Run("CREATE INDEX DEST/CHIDX ON DEST/CHILD(ID)"); Run("CREATE VIEW DEST/CHVIEW AS SELECT ID FROM DEST/CHILD");
        _system.Journals.CreateReceiver(new("DEST", "RCV")); _system.Journals.Create(new("DEST", "JRN"), new("DEST", "RCV"));
        _system.Journals.StartFile(new("DEST", "CHILD"), new("DEST", "JRN"), "*BOTH");
        Scalar("CREATE TABLE seen(n INTEGER); CREATE TRIGGER observe_child AFTER INSERT ON \"DEST.CHILD.CHILD\" BEGIN INSERT INTO seen VALUES(NEW.ID); END");
        var id = Scalar("SELECT id FROM sys_record_slots WHERE lib='DEST' AND name='CHILD'");
        var version = Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE lib='DEST' AND name='CHILD') AND rrn=1");
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Assert.Equal(id, Scalar("SELECT id FROM sys_record_slots WHERE lib='DEST' AND name='CHILD'"));
        Assert.Equal(version, Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE lib='DEST' AND name='CHILD') AND rrn=1"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM seen")); Assert.Empty(_system.Journals.Read(new("DEST", "JRN")));
        using (var query = _files.OpenSqlQuery("SELECT ID FROM DEST/CHVIEW", Options)) { Assert.True(query.Read()); Assert.Equal(1L, query.Current![0]); }
        Run("INSERT INTO DEST/CHILD VALUES(1)");
        Assert.Equal(2, _files.RowCount("DEST", "CHIDX", "CHIDX")); Assert.Equal(1L, Scalar("SELECT n FROM seen"));
        Assert.Equal("PT", Assert.Single(_system.Journals.Read(new("DEST", "JRN"))).Type);
    }

    [Fact]
    public void Establishment_preserves_pending_CHECK_rows_and_restores_CHECK_enforcement()
    {
        RelocationDefinedPair(); Run("INSERT INTO QGPL/PARENT VALUES(-1)");
        Run("ALTER TABLE DEST/CHILD ADD CONSTRAINT POS CHECK(ID>0)");
        Assert.Null(_files.ChangeConstraints("DEST", "CHILD", ["POS"], false)[0].Error);
        Run("INSERT INTO DEST/CHILD VALUES(-1)");
        Assert.True(_files.ChangeConstraints("DEST", "CHILD", ["POS"], true)[0].CheckPending);
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        var definition = _files.GetDefinition("DEST", "CHILD")!;
        Assert.False(definition.SqlTable!.ForeignKeys![0].Defined); Assert.False(definition.SqlTable.ForeignKeys[0].CheckPending);
        Assert.True(definition.SqlTable.Checks![0].CheckPending);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"DEST.CHILD.CHILD\""));
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(-2)")).MessageId);
    }

    [Fact]
    public void Incompatible_parent_copy_remains_defined_until_compatible_attributes_arrive()
    {
        RelocationDefinedPair(); Run("CREATE TABLE QGPL/WRONG(ID BIGINT PRIMARY KEY)");
        _system.ObjectOperations.Relocate(new("QGPL", "WRONG"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("ALTER TABLE DEST/PARENT ALTER ID SET DATA TYPE INTEGER");
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].CheckPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relocation_establishment_rechecks_required_root_and_library_authority(bool library)
    {
        RelocationDefinedPair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "RelocationRevokePassword329");
        foreach (var name in new[] { "QGPL", "DEST" })
            _system.Security.Authority.Grant("QSYS", name, ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        var parent = _system.Objects.GetRequired("QGPL", "PARENT", ObjectType.File); parent.PublicAuthority = AuthorityBit.None; _system.Objects.Update(parent);
        _system.Security.Authority.Grant("QGPL", "PARENT", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.ObjectManagement);
        var mutation = library ? "UPDATE sys_authorities SET bits=0 WHERE lib='QSYS' AND name='DEST' AND holder='QUSER'" :
            "UPDATE sys_authorities SET bits=0 WHERE lib='QGPL' AND name='PARENT' AND holder='QUSER'";
        Scalar("CREATE TRIGGER revoke_relocation AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN " + mutation + "; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
                new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relocation_establishment_rechecks_the_publishing_job_and_rolls_back_state_changes(bool cancel)
    {
        RelocationDefinedPair(); using var session = NativeForeignSession();
        Scalar("CREATE TRIGGER stop_relocation AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN UPDATE sys_jobs SET " +
            (cancel ? "cancel_requested=1" : "execution_state='Succeeded'") + " WHERE number=" + session.Job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key))
        {
            if (cancel) Assert.Throws<OperationCanceledException>(() => _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true));
            else Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
        }
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + session.Job.Key.Number));
    }

    [Fact]
    public void Relocation_establishment_rejects_related_enlisted_record_resources()
    {
        RelocationDefinedPair(); using var session = NativeForeignSession();
        _system.Journals.CreateReceiver(new("DEST", "RCV")); _system.Journals.Create(new("DEST", "JRN"), new("DEST", "RCV"));
        _system.Journals.StartFile(new("DEST", "CHILD"), new("DEST", "JRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        _system.RecordTransactions.Begin(session.Job.Key); _system.RecordTransactions.Enlist(session.Job.Key, new("DEST", "CHILD"));
        try
        {
            Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true));
            Assert.False(_files.FileExists("DEST", "PARENT"));
        }
        finally { _system.RecordTransactions.Rollback(session.Job.Key); _system.RecordTransactions.End(session.Job.Key); }
    }

    [Fact]
    public void Relocation_establishment_rechecks_rows_changed_by_publication_hooks()
    {
        RelocationDefinedPair();
        Scalar("CREATE TRIGGER orphan_relocation AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN INSERT INTO \"DEST.CHILD.CHILD\" VALUES(2); END");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(
            new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true)).MessageId);
        Assert.False(_files.FileExists("DEST", "PARENT")); Assert.Equal(1, _files.RowCount("DEST", "CHILD", "CHILD"));
        Run("INSERT INTO DEST/CHILD VALUES(2)");
    }

    [Fact]
    public void A_copied_zero_member_parent_remains_defined_until_its_member_is_added()
    {
        RelocationDefinedPair(); _files.RemoveMember("QGPL", "PARENT", "PARENT");
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        _files.AddMember("DEST", "PARENT", "REPLACED");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.CheckPending); Assert.Equal("REPLACED", foreign.ParentMember);
    }

    [Fact]
    public void Parent_duplication_restores_partial_SET_NULL_hooks_for_a_defined_composite_reference()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,CONSTRAINT REF FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "CHILD"), copy: true);
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Run("DELETE FROM DEST/PARENT");
        Assert.Equal(1L, Scalar("SELECT A FROM \"DEST.CHILD.CHILD\"")); Assert.Equal(DBNull.Value, Scalar("SELECT B FROM \"DEST.CHILD.CHILD\""));
        Assert.Equal(2L, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
    }

    [Fact]
    public void Establishment_preserves_self_and_external_references_while_adding_the_new_parent_path()
    {
        _system.Libraries.CreateLibrary("DEST"); _system.Libraries.CreateLibrary("EXTERNAL");
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        Run("CREATE TABLE EXTERNAL/OTHER(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO EXTERNAL/OTHER VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER PRIMARY KEY,P INTEGER REFERENCES QGPL/PARENT,O INTEGER REFERENCES EXTERNAL/OTHER,S INTEGER REFERENCES QGPL/CHILD)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,1,1,NULL)");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), ObjectType.File, new("DEST", "CHILD"), copy: true);
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Run("INSERT INTO DEST/CHILD VALUES(2,1,1,1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(3,1,9,1)")).MessageId);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(3,1,1,9)")).MessageId);
        Assert.Equal(2, _files.RowCount("DEST", "CHILD", "CHILD"));
    }

    [Theory]
    [InlineData("CRTDUPOBJ OBJ(PARENT) FROMLIB(QGPL) OBJTYPE(*FILE) TOLIB(DEST) DATA(*YES)")]
    [InlineData("MOVOBJ OBJ(QGPL/PARENT) OBJTYPE(*FILE) TOLIB(DEST)")]
    [InlineData("RNMOBJ OBJ(DEST/OTHER) OBJTYPE(*FILE) NEWOBJ(PARENT)")]
    public void Native_relocation_commands_use_the_shared_automatic_establishment_path(string command)
    {
        RelocationDefinedPair();
        if (command.StartsWith("RNMOBJ", StringComparison.Ordinal))
        { Run("CREATE TABLE DEST/OTHER(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO DEST/OTHER VALUES(1)"); }
        using var session = NativeForeignSession();
        var result = session.Execute(command); Assert.False(result.IsError, result.Message);
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        var orphan = session.Execute("RUNSQL SQL('INSERT INTO DEST/CHILD VALUES(2)') COMMIT(*NONE)");
        Assert.True(orphan.IsError); Assert.Contains("SQL0530", orphan.Message);
    }

    [Fact]
    public void Successful_establishment_preserves_valid_catalog_hooks_and_restores_normal_rename_propagation()
    {
        RelocationDefinedPair();
        Scalar("CREATE TRIGGER valid_relocation AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='DEST' AND NEW.name='CHILD' BEGIN INSERT INTO \"DEST.CHILD.CHILD\" VALUES(1); END");
        _system.ObjectOperations.Relocate(new("QGPL", "PARENT"), ObjectType.File, new("DEST", "PARENT"), copy: true);
        Assert.Equal(2, _files.RowCount("DEST", "CHILD", "CHILD"));
        _system.ObjectOperations.Relocate(new("DEST", "CHILD"), ObjectType.File, new("DEST", "RENAMED"));
        Assert.Contains("DEST.RENAMED.CHILD", (string)Scalar("SELECT sql FROM sqlite_schema WHERE name='valid_relocation'")!);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/RENAMED VALUES(2)")).MessageId);
    }

    [Fact]
    public void Shared_member_rewrite_preserves_catalog_hooks_during_parent_member_removal()
    {
        StatePair();
        Scalar("CREATE TRIGGER valid_removal AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='QGPL' AND NEW.name='CHILD' BEGIN INSERT INTO \"QGPL.CHILD.CHILD\" VALUES(2); END");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }
}
