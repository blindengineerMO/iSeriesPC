using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Late_cascade_locks_validate_the_publishing_job_on_its_own_connection(bool cancel)
    {
        Pair("ON DELETE CASCADE"); Run("INSERT INTO QGPL/PARENT VALUES(2)"); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        using var session = NativeForeignSession();
        Scalar("CREATE TRIGGER change_job BEFORE DELETE ON \"QGPL.PARENT.PARENT\" WHEN OLD.ID=1 BEGIN UPDATE sys_jobs SET cancel_requested=" + (cancel ? "1" : "0") +
            " WHERE number=" + session.Job.Key.Number + "; UPDATE \"QGPL.CHILD.CHILD\" SET ID=1 WHERE ID=2; END");
        using (OperationIdentity.Enter("QSECOFR",session.Job.Key))
        {
            if (cancel) Assert.Throws<OperationCanceledException>(() => Run("DELETE FROM QGPL/PARENT WHERE ID=1"));
            else Assert.Equal(1,Run("DELETE FROM QGPL/PARENT WHERE ID=1"));
        }
        Assert.Equal(cancel ? 2L : 0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal(cancel ? 2L : 1L,Scalar("SELECT count(*) FROM \"QGPL.PARENT.PARENT\""));
        Assert.Equal(0L,Scalar("SELECT cancel_requested FROM sys_jobs WHERE number=" + session.Job.Key.Number));
    }
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public void Dependents_changed_by_a_publication_hook_still_require_record_locks(string rule)
    {
        Pair("ON DELETE " + rule); Run("ALTER TABLE QGPL/CHILD ALTER ID SET DEFAULT NULL");
        Run("INSERT INTO QGPL/PARENT VALUES(2)"); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        Scalar("CREATE TRIGGER change_dependents BEFORE DELETE ON \"QGPL.PARENT.PARENT\" WHEN OLD.ID=1 BEGIN UPDATE \"QGPL.CHILD.CHILD\" SET ID=1 WHERE ID=2; END");
        using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key))
        {
            child = _files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); Assert.NotNull(child.Read());
        }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT WHERE ID=1")).MessageId);
            Assert.Equal(2L,Scalar("SELECT count(*) FROM \"QGPL.PARENT.PARENT\""));
            Assert.Equal(2L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\" WHERE _rowid_=2"));
            child.Dispose(); Assert.Equal(1,Run("DELETE FROM QGPL/PARENT WHERE ID=1"));
        }
    }
    [Fact]
    public void Empty_cascade_paths_still_protect_their_grandchild_file_allocations()
    {
        Pair("ON DELETE CASCADE"); Run("DELETE FROM QGPL/CHILD");
        Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT CK UNIQUE(ID)");
        Run("CREATE TABLE QGPL/GRANDCHILD(ID INTEGER REFERENCES QGPL/CHILD(ID) ON DELETE CASCADE)");
        using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); IDisposable allocation;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) allocation = new JobLockStore(_system.Connections).Acquire(holder.Job.Key,new("QGPL","GRANDCHILD",ObjectType.File),JobLockMode.Exclusive,TimeSpan.Zero);
        using (allocation)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key)) Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT")).MessageId);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
    }
    [Fact]
    public void Self_cascades_protect_descendants_outside_the_direct_statement_selection()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER REFERENCES QGPL/TREE ON DELETE CASCADE)");
        Run("INSERT INTO QGPL/TREE VALUES(1,NULL),(2,1),(3,2)");
        using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key))
        {
            child = _files.OpenUpdateCursor("QGPL","TREE","TREE"); Assert.NotNull(child.Read()); Assert.NotNull(child.Read()); Assert.NotNull(child.Read());
        }
        using (child)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/TREE WHERE ID=1")).MessageId);
            child.Dispose(); Assert.Equal(1,Run("DELETE FROM QGPL/TREE WHERE ID=1"));
            Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.TREE.TREE\""));
        }
    }
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public void Cascading_SQL_actions_respect_another_jobs_retained_child_record(string rule)
    {
        Pair("ON DELETE " + rule); Run("ALTER TABLE QGPL/CHILD ALTER ID SET DEFAULT NULL");
        using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { cursor = _files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(cursor.Read()); }
        using (cursor)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        {
            Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT")).MessageId);
            Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
        }
    }
    [Fact]
    public void A_native_parent_cursor_holds_cascade_locks_until_its_transaction_finishes()
    {
        Pair("ON DELETE CASCADE"); using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); DatabaseRecordCursor child;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) { child = _files.OpenUpdateCursor("QGPL","CHILD","CHILD"); Assert.NotNull(child.Read()); }
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key))
        using (var parent = _files.OpenUpdateCursor("QGPL","PARENT","PARENT"))
        {
            Assert.NotNull(parent.Read()); var error = Assert.Throws<CpfException>(parent.Delete); Assert.Equal("CPF1002",error.MessageId); Assert.True(DatabaseRecordErrors.IsRecordLockFailure(error));
            child.Dispose(); parent.Delete();
        }
        child.Dispose(); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Cascades_delegate_child_changes_without_granting_the_caller_child_data_access()
    {
        Pair("ON DELETE CASCADE"); using var session = ForeignUser();
        var child = _system.Objects.GetRequired("QGPL","CHILD",ObjectType.File); child.PublicAuthority = AuthorityBit.None; _system.Objects.Update(child);
        _system.Security.Authority.Grant("QGPL","PARENT",ObjectType.File,"QUSER",AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Delete);
        using (OperationIdentity.Enter("QUSER",session.Job.Key))
        {
            Assert.Equal("SQL0551",Assert.Throws<CpfException>(() => _files.OpenSqlQuery("SELECT ID FROM QGPL/CHILD",Options)).MessageId);
            Assert.Equal(1,Run("DELETE FROM QGPL/PARENT WHERE ID=1"));
        }
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Indirect_catalog_changes_roll_back_the_parent_and_all_cascaded_records()
    {
        Pair("ON DELETE CASCADE"); using var session = NativeForeignSession(); var before = _files.GetDefinition("QGPL","CHILD")!.ToJson();
        Scalar("CREATE TRIGGER corrupt_cascade AFTER DELETE ON \"QGPL.CHILD.CHILD\" BEGIN UPDATE sys_file_defs SET def=json_set(def,'$.name','CHANGED') WHERE name='CHILD'; END");
        using (OperationIdentity.Enter("QSECOFR",session.Job.Key))
            Assert.Equal("SQL0104",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT")).MessageId);
        Assert.Equal(before,_files.GetDefinition("QGPL","CHILD")!.ToJson());
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void A_large_cascade_uses_bounded_lock_holders_and_releases_them_after_publication()
    {
        Pair("ON DELETE CASCADE"); Run("INSERT INTO QGPL/CHILD VALUES " + string.Join(',',Enumerable.Repeat("(1)",8300)));
        using var session = NativeForeignSession(); using var identity = OperationIdentity.Enter("QSECOFR",session.Job.Key);
        Assert.Equal(1,Run("DELETE FROM QGPL/PARENT")); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL","CHILD",ObjectType.File)),item => item.Job == session.Job.Key);
    }
    [Fact]
    public void A_child_file_allocation_is_respected_even_when_no_dependent_records_exist()
    {
        Pair(); Run("DELETE FROM QGPL/CHILD"); using var holder = NativeForeignSession(); using var writer = NativeForeignSession(); IDisposable allocation;
        using (OperationIdentity.Enter("QSECOFR",holder.Job.Key)) allocation = new JobLockStore(_system.Connections).Acquire(holder.Job.Key,new("QGPL","CHILD",ObjectType.File),JobLockMode.Exclusive,TimeSpan.Zero);
        using (allocation)
        using (OperationIdentity.Enter("QSECOFR",writer.Job.Key)) Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT")).MessageId);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
    }
}
