using Ipc.Core.Messages;
using Ipc.Db.Store;
using Ipc.Db.Dds;
using Ipc.Services.Events;
using Ipc.Session;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private void Pair(string suffix = "")
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER CONSTRAINT PK PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER,CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT " + suffix + ")");
        Run("INSERT INTO QGPL/CHILD VALUES(1)");
    }
    [Fact]
    public void Unrelated_parent_and_child_ALTER_preserve_native_enforcement()
    {
        Pair(); Run("ALTER TABLE QGPL/PARENT ADD EXTRA INTEGER DEFAULT 7"); Run("ALTER TABLE QGPL/CHILD ADD EXTRA INTEGER DEFAULT 8");
        Assert.Equal(7L, Scalar("SELECT EXTRA FROM \"QGPL.PARENT.PARENT\""));
        Assert.Equal(8L, Scalar("SELECT EXTRA FROM \"QGPL.CHILD.CHILD\""));
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD(ID) VALUES(2)")).MessageId);
    }
    [Fact]
    public void Dropping_a_parent_key_restricts_or_atomically_removes_only_the_dependent_constraint()
    {
        Pair(); Run("CREATE INDEX QGPL/INDEX ON QGPL/CHILD(ID)");
        Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/PARENT DROP PRIMARY KEY RESTRICT")).MessageId);
        Run("ALTER TABLE QGPL/PARENT DROP CONSTRAINT PK CASCADE");
        Assert.Null(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys); Assert.Equal(1, _files.RowCount("QGPL", "INDEX", "INDEX"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)"); Assert.Equal(2, _files.RowCount("QGPL", "INDEX", "INDEX"));
    }
    [Fact]
    public void SQL_DROP_parent_keeps_child_records_and_removes_its_foreign_key()
    {
        Pair("ON DELETE CASCADE"); Run("DROP TABLE QGPL/PARENT");
        Assert.False(_files.FileExists("QGPL", "PARENT")); Assert.Equal(1L, Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        Assert.Null(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys); Run("INSERT INTO QGPL/CHILD VALUES(2)");
    }
    [Fact]
    public void Self_referencing_keys_cascade_schema_drops_without_dropping_rows()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER REFERENCES QGPL/TREE)"); Run("INSERT INTO QGPL/TREE VALUES(1,1)");
        Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/TREE DROP PRIMARY KEY RESTRICT")).MessageId);
        Run("ALTER TABLE QGPL/TREE DROP PRIMARY KEY CASCADE"); Run("INSERT INTO QGPL/TREE VALUES(2,9)");
        Assert.Null(_files.GetDefinition("QGPL", "TREE")!.SqlTable!.ForeignKeys);
    }
    [Fact]
    public void Partial_SET_NULL_hooks_survive_parent_rewrites_and_disappear_with_the_child_constraint()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,CONSTRAINT REF FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); Run("ALTER TABLE QGPL/PARENT ADD EXTRA INTEGER"); Run("DELETE FROM QGPL/PARENT");
        Assert.Equal(1L, Scalar("SELECT A FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(DBNull.Value, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
        Run("ALTER TABLE QGPL/CHILD DROP CONSTRAINT REF");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name GLOB 'ipc_sql_fk_null_*'"));
    }
    [Fact]
    public void Native_delete_and_member_limit_changes_cannot_leave_broken_relationships()
    {
        Pair();
        Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections).Delete(new("QGPL", "PARENT"), Ipc.Core.Objects.ObjectType.File)).MessageId);
        Assert.Throws<CpfException>(() => _files.ChangePhysicalMemberLimit("QGPL", "PARENT", 2));
        Assert.Throws<CpfException>(() => _files.ChangePhysicalMemberLimit("QGPL", "CHILD", 2));
        Run("INSERT INTO QGPL/CHILD VALUES(NULL)");
    }
    [Fact]
    public void Physical_logical_and_cursor_writes_use_the_same_foreign_keys()
    {
        Pair();
        var source = LogicalFileTests.Line('R', "CHILDREC", "PFILE(QGPL/CHILD)") + "\n" + LogicalFileTests.Line('K', "ID");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "CHILD"), _files.GetDefinition("QGPL", "CHILD")!)).Compile("LOGICAL", source);
        _files.CreateLogicalFile("QGPL", "LOGICAL", definition, source);
        Assert.Equal(787, Assert.Throws<SqliteException>(() => _files.Insert("QGPL", "CHILD", "CHILD", "CHILD", Values(2))).SqliteExtendedErrorCode);
        Assert.Equal(787, Assert.Throws<SqliteException>(() => _files.Insert("QGPL", "LOGICAL", "LOGICAL", "CHILDREC", Values(2))).SqliteExtendedErrorCode);
        using var cursor = _files.OpenSequentialCursor("QGPL", "CHILD", "CHILD"); Assert.NotNull(cursor.Read());
        Assert.Equal(787, Assert.Throws<SqliteException>(() => cursor.Update(Values(2))).SqliteExtendedErrorCode);
        Assert.Equal(1L, Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Private_workspaces_stage_cascades_and_publish_complete_valid_units()
    {
        Pair("ON DELETE CASCADE");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ForeignUnitAdmin329");
        using var session = new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None);
        _system.Journals.CreateReceiver(new("QGPL","FKRCV")); _system.Journals.Create(new("QGPL","FKJRN"),new("QGPL","FKRCV"));
        foreach (var file in new[] { "PARENT","CHILD" }) _system.Journals.StartFile(new("QGPL",file),new("QGPL","FKJRN"),"*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR",session.Job.Key);
        _system.RecordTransactions.Begin(session.Job.Key);
        foreach (var file in new[] { "PARENT","CHILD" }) _system.RecordTransactions.Enlist(session.Job.Key,new("QGPL",file));
        try
        {
            Assert.Equal(787,Assert.Throws<SqliteException>(() => _files.Insert("QGPL","CHILD","CHILD","CHILD",Values(2))).SqliteExtendedErrorCode);
            using (var unit = _system.RecordTransactions.Open(session.Job.Key)) { unit.Execute("DELETE FROM \"QGPL.PARENT.PARENT\""); unit.Complete(); }
            Assert.Equal(0,_files.RowCount("QGPL","CHILD","CHILD")); Assert.Equal(1L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
            _system.RecordTransactions.Commit(session.Job.Key);
        }
        finally { _system.RecordTransactions.Rollback(session.Job.Key); _system.RecordTransactions.End(session.Job.Key); }
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    private static Dictionary<string,object?> Values(long value) => new() { ["ID"] = value };
}
