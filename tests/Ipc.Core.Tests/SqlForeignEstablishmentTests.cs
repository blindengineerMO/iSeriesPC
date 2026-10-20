using Ipc.Core.Messages;
using Ipc.Db.Definitions;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Fact]
    public void Creating_a_missing_parent_automatically_establishes_the_copy_and_marks_mismatched_rows_pending()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), Ipc.Core.Objects.ObjectType.File, new("DEST", "CHILD"), copy: true);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.CheckPending); Assert.Equal("PARENT", foreign.ParentMember);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("DEST", "CHILD", "CHILD")).MessageId);
        Run("INSERT INTO DEST/PARENT VALUES(1)");
        Assert.Null(Assert.Single(_files.ChangeConstraints("DEST", "CHILD", [foreign.Name!], enabled: true)).Error);
        Assert.Equal(1, _files.RowCount("DEST", "CHILD", "CHILD"));
    }

    [Fact]
    public void Creating_a_parent_establishes_an_empty_dependent_without_pending_state()
    {
        StatePair(); Run("DELETE FROM QGPL/CHILD"); _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), Ipc.Core.Objects.ObjectType.File, new("DEST", "CHILD"), copy: true);
        Run("CREATE TABLE DEST/PARENT(ID INTEGER PRIMARY KEY)");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0]; Assert.False(foreign.Defined); Assert.False(foreign.CheckPending);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(1)")).MessageId);
        Run("INSERT INTO DEST/PARENT VALUES(1)"); Run("INSERT INTO DEST/CHILD VALUES(1)");
    }

    [Fact]
    public void Adding_the_dependent_member_establishes_its_defined_relationship()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST");
        var definition = FileDefinition.FromJson(_files.GetDefinition("QGPL", "CHILD")!.ToJson());
        definition.SqlTable = definition.SqlTable! with { ForeignKeys = definition.SqlTable!.ForeignKeys!.Select(item => item with { Defined = true, ParentMember = null }).ToArray() };
        _files.CreatePhysicalFile("DEST", "CHILD", definition, "", member: "*NONE");
        _files.AddMember("DEST", "CHILD", "FIRST");
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("INSERT INTO DEST/CHILD VALUES(1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
    }

    [Fact]
    public void Adding_the_parent_member_uses_its_actual_name_and_checks_existing_child_data()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST");
        var parent = FileDefinition.FromJson(_files.GetDefinition("QGPL", "PARENT")!.ToJson());
        _files.CreatePhysicalFile("DEST", "PARENT", parent, "", member: "*NONE");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), Ipc.Core.Objects.ObjectType.File, new("DEST", "CHILD"), copy: true);
        _files.AddMember("DEST", "PARENT", "NEWPARENT");
        var foreign = _files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.CheckPending); Assert.Equal("NEWPARENT", foreign.ParentMember);
        Run("INSERT INTO DEST/PARENT VALUES(1)"); Assert.Null(Assert.Single(_files.ChangeConstraints("DEST", "CHILD", [foreign.Name!], enabled: true)).Error);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
    }

    [Fact]
    public void Adding_a_compatible_parent_key_automatically_establishes_defined_relationships()
    {
        StatePair(); _system.Libraries.CreateLibrary("DEST"); Run("CREATE TABLE DEST/PARENT(ID INTEGER)");
        Run("INSERT INTO DEST/PARENT VALUES(1)");
        _system.ObjectOperations.Relocate(new("QGPL", "CHILD"), Ipc.Core.Objects.ObjectType.File, new("DEST", "CHILD"), copy: true);
        Assert.True(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("ALTER TABLE DEST/PARENT ADD PRIMARY KEY(ID)");
        Assert.False(_files.GetDefinition("DEST", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO DEST/CHILD VALUES(2)")).MessageId);
    }
}
