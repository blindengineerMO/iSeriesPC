using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Theory]
    [InlineData("PARENT", "PARENT")]
    [InlineData("PARENT", "CHILD")]
    [InlineData("CHILD", "PARENT")]
    [InlineData("CHILD", "CHILD")]
    public void Member_removal_rejects_retained_related_queries_and_releases_its_leases(string removed, string opened)
    {
        StatePair(); using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/" + opened, Options))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", removed, removed)).MessageId);
        Assert.True(_files.MemberExists("QGPL", removed, removed));
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        _files.RemoveMember("QGPL", removed, removed);
        using var locks = _system.Connections.Locks.Open(); using var read = locks.CreateCommand();
        read.CommandText = "SELECT count(*) FROM holders WHERE lifetime='Referential'"; Assert.Equal(0L, read.ExecuteScalar());
    }

    [Theory]
    [InlineData("PARENT", false)]
    [InlineData("CHILD", false)]
    [InlineData("PARENT", true)]
    [InlineData("CHILD", true)]
    public void Member_removal_delegates_related_changes_using_only_root_existence_and_operate_rights(string removed, bool command)
    {
        StatePair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "MemberUserPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        foreach (var name in new[] { "PARENT", "CHILD" })
        { var descriptor = _system.Objects.GetRequired("QGPL", name, ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor); }
        _system.Security.Authority.Grant("QGPL", removed, ObjectType.File, "QUSER", AuthorityBit.ObjectExist | AuthorityBit.ObjectOperate);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
        {
            if (command) { var result = session.Execute("RMVM FILE(QGPL/" + removed + ") MBR(" + removed + ")"); Assert.False(result.IsError, result.Message); }
            else _system.ObjectOperations.RemoveFileMember(new("QGPL", removed), removed);
        }
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.False(_files.MemberExists("QGPL", removed, removed));
    }

    [Theory]
    [InlineData("UPDATE sys_objects SET owner='QUSER' WHERE lib='QGPL' AND name='PARENT' AND type='*FILE'")]
    [InlineData("UPDATE sys_record_slots SET id='changed-id' WHERE lib='QGPL' AND name='CHILD'")]
    [InlineData("UPDATE sys_file_members SET mbr='CHANGED' WHERE lib='QGPL' AND name='CHILD'")]
    [InlineData("UPDATE sys_file_defs SET def=json_set(def,'$.maximumMembers',2) WHERE lib='QGPL' AND name='PARENT'")]
    public void Member_removal_rolls_back_late_related_identity_and_definition_changes(string mutation)
    {
        StatePair(); var before = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        Scalar("CREATE TRIGGER change_rmvm AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='QGPL' AND NEW.name='CHILD' BEGIN " + mutation + "; END");
        Assert.Equal("CPF32B2", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT")); Assert.Equal(before, _files.GetDefinition("QGPL", "CHILD")!.ToJson());
        Assert.Equal(1, _files.RowCount("QGPL", "CHILD", "CHILD"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'ipc_rmvm_*'"));
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
    }

    [Theory]
    [InlineData(AuthorityBit.ObjectExist)]
    [InlineData(AuthorityBit.ObjectOperate)]
    [InlineData(AuthorityBit.None)]
    public void Member_removal_rechecks_required_authority_after_delegated_hooks(AuthorityBit revoked)
    {
        StatePair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "MemberRevokePassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        _system.Security.Authority.Grant("QGPL", "PARENT", ObjectType.File, "QUSER", AuthorityBit.ObjectExist | AuthorityBit.ObjectOperate);
        foreach (var name in new[] { "PARENT", "CHILD" })
        { var descriptor = _system.Objects.GetRequired("QGPL", name, ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor); }
        var update = revoked == AuthorityBit.None ? "UPDATE sys_authorities SET bits=0 WHERE lib='QSYS' AND name='QGPL' AND holder='QUSER'" :
            "UPDATE sys_authorities SET bits=bits & " + ~(long)revoked + " WHERE lib='QGPL' AND name='PARENT' AND holder='QUSER'";
        Scalar("CREATE TRIGGER revoke_rmvm AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='QGPL' AND NEW.name='CHILD' BEGIN " + update + "; END");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT"));
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData(AuthorityBit.ObjectManagement)]
    [InlineData(AuthorityBit.ObjectOperate)]
    [InlineData(AuthorityBit.ObjectExist)]
    [InlineData(AuthorityBit.ObjectManagement | AuthorityBit.ObjectOperate)]
    [InlineData(AuthorityBit.ObjectManagement | AuthorityBit.ObjectExist)]
    public void Member_removal_requires_both_native_existence_and_operate_permissions(AuthorityBit granted)
    {
        StatePair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "MemberDeniedPassword329");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "QUSER", AuthorityBit.ObjectOperate);
        var descriptor = _system.Objects.GetRequired("QGPL", "PARENT", ObjectType.File); descriptor.PublicAuthority = AuthorityBit.None; _system.Objects.Update(descriptor);
        _system.Security.Authority.Grant("QGPL", "PARENT", ObjectType.File, "QUSER", granted);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using (OperationIdentity.Enter("QUSER", session.Job.Key))
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        var result = session.Execute("RMVM FILE(QGPL/PARENT) MBR(PARENT)"); Assert.True(result.IsError); Assert.Contains("CPF9802", result.Message);
        Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT"));
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Fact]
    public void Removing_a_parent_preserves_pending_CHECK_rows_and_restores_CHECK_enforcement()
    {
        StatePair(); Run("INSERT INTO QGPL/PARENT VALUES(-1)"); Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT POS CHECK(ID>0)");
        Assert.Null(State("CHILD", false, "POS").Error); Run("INSERT INTO QGPL/CHILD VALUES(-1)");
        Assert.True(State("CHILD", true, "POS").CheckPending);
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.Checks![0].CheckPending);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(-2)")).MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "CHILD", "CHILD")).MessageId);
    }

    [Fact]
    public void Removing_a_pending_parent_releases_dependent_pending_restrictions_while_defined()
    {
        StatePair(); Assert.Null(State("CHILD", false).Error); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        Assert.True(State("CHILD", true).CheckPending);
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD")); Run("INSERT INTO QGPL/CHILD VALUES(3)");
    }

    [Fact]
    public void Member_removal_preserves_surviving_RRNs_versions_indexes_and_user_and_journal_triggers()
    {
        StatePair(); Run("CREATE INDEX QGPL/CHIDX ON QGPL/CHILD(ID)");
        _system.Journals.CreateReceiver(new("QGPL", "RMRCV")); _system.Journals.Create(new("QGPL", "RMJRN"), new("QGPL", "RMRCV"));
        _system.Journals.StartFile(new("QGPL", "CHILD"), new("QGPL", "RMJRN"), "*BOTH");
        Scalar("CREATE TABLE seen(n INTEGER); CREATE TRIGGER child_write AFTER INSERT ON \"QGPL.CHILD.CHILD\" BEGIN INSERT INTO seen VALUES(NEW.ID); END");
        var identity = Scalar("SELECT id FROM sys_record_slots WHERE name='CHILD'");
        var version = Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='CHILD') AND rrn=1");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.Equal(identity, Scalar("SELECT id FROM sys_record_slots WHERE name='CHILD'"));
        Assert.Equal(version, Scalar("SELECT version FROM sys_record_versions WHERE member_id=(SELECT id FROM sys_record_slots WHERE name='CHILD') AND rrn=1"));
        Assert.Equal(1, _files.RowCount("QGPL", "CHIDX", "CHIDX")); Assert.Empty(_system.Journals.Read(new("QGPL", "RMJRN")));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM seen"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)");
        Assert.Equal(2, _files.RowCount("QGPL", "CHIDX", "CHIDX")); Assert.Equal(2L, Scalar("SELECT n FROM seen"));
        Assert.Equal("PT", Assert.Single(_system.Journals.Read(new("QGPL", "RMJRN"))).Type);
        Assert.Equal(2L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='CHILD'"));
    }

    [Fact]
    public void Member_removal_preserves_views_over_surviving_dependents()
    {
        StatePair(); Run("CREATE VIEW QGPL/CHVIEW AS SELECT ID FROM QGPL/CHILD");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        using var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/CHVIEW", Options);
        Assert.True(query.Read()); Assert.Equal(1L, query.Current![0]); Assert.False(query.Read());
    }

    [Fact]
    public void Member_removal_does_not_accept_unregistered_SQL_dependents()
    {
        StatePair(); Scalar("CREATE VIEW unregistered AS SELECT ID FROM \"QGPL.PARENT.PARENT\"");
        Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT"));
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
    }

    [Theory]
    [InlineData("PARENT")]
    [InlineData("CHILD")]
    public void Member_removal_rejects_enlisted_related_record_resources(string enlisted)
    {
        StatePair(); using var session = NativeForeignSession();
        _system.Journals.CreateReceiver(new("QGPL", "RMRCV")); _system.Journals.Create(new("QGPL", "RMJRN"), new("QGPL", "RMRCV"));
        _system.Journals.StartFile(new("QGPL", enlisted), new("QGPL", "RMJRN"), "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        _system.RecordTransactions.Begin(session.Job.Key); _system.RecordTransactions.Enlist(session.Job.Key, new("QGPL", enlisted));
        try
        {
            Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT"));
            Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT"));
        }
        finally { _system.RecordTransactions.Rollback(session.Job.Key); _system.RecordTransactions.End(session.Job.Key); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Member_removal_rechecks_the_publishing_jobs_live_state(bool cancel)
    {
        StatePair(); using var session = NativeForeignSession();
        Scalar("CREATE TRIGGER stop_rmvm AFTER UPDATE OF def ON sys_file_defs WHEN NEW.lib='QGPL' AND NEW.name='CHILD' BEGIN UPDATE sys_jobs SET " +
            (cancel ? "cancel_requested=1" : "execution_state='Succeeded'") + " WHERE number=" + session.Job.Key.Number + "; END");
        using (OperationIdentity.Enter("QSECOFR", session.Job.Key))
        {
            if (cancel) Assert.Throws<OperationCanceledException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT"));
            else Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        }
        Assert.True(_files.MemberExists("QGPL", "PARENT", "PARENT"));
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(0L, Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + session.Job.Key.Number));
    }

    [Fact]
    public void Member_removal_removes_and_reestablishment_restores_partial_SET_NULL_hooks()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,CONSTRAINT REF FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name GLOB 'ipc_sql_fk_null_*'"));
        Assert.Equal(2L, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
        _files.AddMember("QGPL", "PARENT", "REPLACED"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Assert.Null(State("CHILD", true).Error); Run("DELETE FROM QGPL/PARENT");
        Assert.Equal(1L, Scalar("SELECT A FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(DBNull.Value, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
    }

    [Fact]
    public void Member_removal_keeps_unaffected_foreign_and_self_references_and_views()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        Run("CREATE TABLE QGPL/OTHER(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/OTHER VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER PRIMARY KEY,P INTEGER REFERENCES QGPL/PARENT,O INTEGER REFERENCES QGPL/OTHER,S INTEGER REFERENCES QGPL/CHILD)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,1,1,NULL),(2,1,1,1)");
        Run("CREATE VIEW QGPL/CHVIEW AS SELECT ID FROM QGPL/CHILD");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Run("INSERT INTO QGPL/CHILD VALUES(3,9,1,2)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(4,9,9,1)")).MessageId);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(4,9,1,9)")).MessageId);
        using var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/CHVIEW", Options);
        var count = 0; while (query.Read()) count++; Assert.Equal(3, count);
    }

    [Fact]
    public void Member_removal_rejects_retained_queries_over_a_surviving_child_index()
    {
        StatePair(); Run("CREATE INDEX QGPL/CHIDX ON QGPL/CHILD(ID)");
        using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        using (var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/CHIDX", Options))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _files.RemoveMember("QGPL", "PARENT", "PARENT")).MessageId);
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.Equal(1, _files.RowCount("QGPL", "CHIDX", "CHIDX"));
    }
}
