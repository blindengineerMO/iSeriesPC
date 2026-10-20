using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Services.Events;
using Ipc.Session;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Fact]
    public void Renaming_and_moving_a_parent_rebinds_child_metadata_and_keeps_enforcement()
    {
        Pair(); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL","PARENT"),ObjectType.File,new("QGPL","RENAMED"));
        Assert.Equal("RENAMED",Assert.Single(_files.GetDefinition("QGPL","CHILD")!.SqlTable!.ForeignKeys!).Parent.Name);
        _system.ObjectOperations.Relocate(new("QGPL","RENAMED"),ObjectType.File,new("DEST","MOVED"));
        var reference = Assert.Single(_files.GetDefinition("QGPL","CHILD")!.SqlTable!.ForeignKeys!);
        Assert.Equal("DEST",reference.Parent.Schema); Assert.Equal("MOVED",reference.Parent.Name); Assert.Equal("PARENT",reference.ParentMember);
        Assert.Equal("SQL0532",Assert.Throws<CpfException>(() => Run("DELETE FROM DEST/MOVED")).MessageId);
        Run("ALTER TABLE QGPL/CHILD ADD EXTRA INTEGER");
        Assert.Equal("SQL0530",Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD(ID) VALUES(2)")).MessageId);
    }
    [Fact]
    public void Moving_a_child_keeps_its_established_parent_and_recreates_partial_null_hooks()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,CONSTRAINT REF FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL","CHILD"),ObjectType.File,new("DEST","MOVED"));
        var reference = Assert.Single(_files.GetDefinition("DEST","MOVED")!.SqlTable!.ForeignKeys!); Assert.Equal("QGPL",reference.Parent.Schema);
        Run("DELETE FROM QGPL/PARENT");
        Assert.Equal(1L,Scalar("SELECT A FROM \"DEST.MOVED.CHILD\"")); Assert.Equal(DBNull.Value,Scalar("SELECT B FROM \"DEST.MOVED.CHILD\""));
        Run("ALTER TABLE DEST/MOVED ADD EXTRA INTEGER");
        Assert.Equal(1L,Scalar("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name GLOB 'ipc_sql_fk_null_*'"));
    }
    [Fact]
    public void Copying_a_self_reference_renames_constraints_and_isolates_cascades()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER CONSTRAINT SELFREF REFERENCES QGPL/TREE ON DELETE CASCADE)");
        Run("INSERT INTO QGPL/TREE VALUES(1,NULL),(2,1)");
        _system.ObjectOperations.Relocate(new("QGPL","TREE"),ObjectType.File,new("QGPL","COPIED"),copy:true);
        var reference = Assert.Single(_files.GetDefinition("QGPL","COPIED")!.SqlTable!.ForeignKeys!);
        Assert.NotEqual("SELFREF",reference.Name); Assert.Equal("COPIED",reference.Parent.Name); Assert.Equal("TREE",reference.ParentMember);
        Run("ALTER TABLE QGPL/COPIED ADD EXTRA INTEGER"); Run("DELETE FROM QGPL/COPIED WHERE ID=1");
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.COPIED.TREE\"")); Assert.Equal(2L,Scalar("SELECT count(*) FROM \"QGPL.TREE.TREE\""));
    }
    [Fact]
    public void Copying_a_child_rebinds_same_library_parent_names_in_the_destination()
    {
        Pair("ON DELETE CASCADE"); _system.Libraries.CreateLibrary("DEST");
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO DEST/PARENT VALUES(1)");
        _system.ObjectOperations.Relocate(new("QGPL","CHILD"),ObjectType.File,new("DEST","COPIED"),copy:true);
        Assert.Equal("DEST",Assert.Single(_files.GetDefinition("DEST","COPIED")!.SqlTable!.ForeignKeys!).Parent.Schema);
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(1L,Scalar("SELECT count(*) FROM \"DEST.COPIED.CHILD\""));
        Run("DELETE FROM DEST/PARENT"); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"DEST.COPIED.CHILD\""));
    }
    [Fact]
    public void Copying_a_child_retains_a_parent_that_was_in_a_different_library()
    {
        _system.Libraries.CreateLibrary("PARENTS"); _system.Libraries.CreateLibrary("DEST");
        Run("CREATE TABLE PARENTS/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO PARENTS/PARENT VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER REFERENCES PARENTS/PARENT ON DELETE CASCADE)"); Run("INSERT INTO QGPL/CHILD VALUES(1)");
        _system.ObjectOperations.Relocate(new("QGPL","CHILD"),ObjectType.File,new("DEST","COPIED"),copy:true);
        Assert.Equal("PARENTS",Assert.Single(_files.GetDefinition("DEST","COPIED")!.SqlTable!.ForeignKeys!).Parent.Schema);
        Run("DELETE FROM PARENTS/PARENT"); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"DEST.COPIED.CHILD\""));
    }
    [Fact]
    public void Copying_a_parent_does_not_copy_hooks_that_belong_to_other_child_objects()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)");
        _system.ObjectOperations.Relocate(new("QGPL","PARENT"),ObjectType.File,new("QGPL","COPIED"),copy:true);
        Run("DELETE FROM QGPL/COPIED"); Assert.Equal(2L,Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(DBNull.Value,Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Library_rename_updates_internal_and_external_foreign_keys_as_one_operation()
    {
        _system.Libraries.CreateLibrary("OLDLIB");
        Run("CREATE TABLE OLDLIB/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO OLDLIB/PARENT VALUES(1)");
        Run("CREATE TABLE OLDLIB/CHILD(ID INTEGER REFERENCES OLDLIB/PARENT ON DELETE CASCADE)"); Run("INSERT INTO OLDLIB/CHILD VALUES(1)");
        Run("CREATE TABLE QGPL/OUTSIDE(ID INTEGER REFERENCES OLDLIB/PARENT ON DELETE CASCADE)"); Run("INSERT INTO QGPL/OUTSIDE VALUES(1)");
        _system.ObjectOperations.Relocate(new("QSYS","OLDLIB"),ObjectType.Library,new("QSYS","NEWLIB"));
        foreach (var key in new[] { ("NEWLIB","CHILD"),("QGPL","OUTSIDE") })
            Assert.Equal("NEWLIB",Assert.Single(_files.GetDefinition(key.Item1,key.Item2)!.SqlTable!.ForeignKeys!).Parent.Schema);
        Run("DELETE FROM NEWLIB/PARENT");
        Assert.Equal(0L,Scalar("SELECT count(*) FROM \"NEWLIB.CHILD.CHILD\"")); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.OUTSIDE.OUTSIDE\""));
    }
    [Fact]
    public void Failed_copy_publication_restores_source_schema_metadata_and_hooks()
    {
        Pair("ON DELETE CASCADE"); var before = _files.GetDefinition("QGPL","CHILD")!.ToJson();
        Scalar("CREATE TRIGGER fail_foreign_copy BEFORE INSERT ON sys_file_defs WHEN NEW.name='COPIED' BEGIN SELECT RAISE(ABORT,'copy unavailable'); END");
        Assert.Throws<SqliteException>(() => _system.ObjectOperations.Relocate(new("QGPL","CHILD"),ObjectType.File,new("QGPL","COPIED"),copy:true));
        Assert.False(_files.FileExists("QGPL","COPIED")); Assert.Equal(before,_files.GetDefinition("QGPL","CHILD")!.ToJson());
        Assert.Equal(0L,Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.COPIED.CHILD'"));
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(0L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void A_retained_child_query_blocks_parent_relocation_before_any_identity_changes()
    {
        Pair(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"ForeignMoveAdmin329");
        using var session = new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None);
        using var identity = OperationIdentity.Enter("QSECOFR",session.Job.Key); using var query = _files.OpenSqlQuery("SELECT ID FROM QGPL/CHILD",Options);
        Assert.True(query.Read());
        Assert.Equal("CPF1002",Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL","PARENT"),ObjectType.File,new("QGPL","MOVED"))).MessageId);
        Assert.True(_files.FileExists("QGPL","PARENT")); Assert.Equal("PARENT",Assert.Single(_files.GetDefinition("QGPL","CHILD")!.SqlTable!.ForeignKeys!).Parent.Name);
    }
}
