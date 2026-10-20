using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Theory]
    [InlineData("*ALL")]
    [InlineData("PAR*")]
    public void RMVM_selection_defines_references_and_reestablishes_a_replacement_parent(string selector)
    {
        StatePair(); using var session = NativeForeignSession();
        var result = session.Execute("RMVM QGPL/PARENT " + selector); Assert.False(result.IsError, result.Message);
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(1, _files.RowCount("QGPL", "CHILD", "CHILD"));
        _files.AddMember("QGPL", "PARENT", "REPLACED");
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].CheckPending);
    }

    [Fact]
    public void Removing_a_parent_member_preserves_child_rows_and_paths_and_defines_the_relationship()
    {
        StatePair();
        var indexes = Scalar("SELECT group_concat(name) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.CHILD.CHILD'");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        var foreign = _files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.True(foreign.Defined); Assert.Null(foreign.ParentMember); Assert.False(foreign.Disabled);
        Assert.Equal(1, _files.RowCount("QGPL", "CHILD", "CHILD"));
        Assert.Equal(indexes, Scalar("SELECT group_concat(name) FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.CHILD.CHILD'"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)");
        _files.AddMember("QGPL", "PARENT", "REPLACED");
        foreign = _files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.Equal("REPLACED", foreign.ParentMember); Assert.True(foreign.CheckPending);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "CHILD", "CHILD")).MessageId);
    }

    [Fact]
    public void Removing_a_child_member_preserves_parent_data_and_establishes_its_replacement_member()
    {
        StatePair(); _files.RemoveMember("QGPL", "CHILD", "CHILD");
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(1, _files.RowCount("QGPL", "PARENT", "PARENT"));
        _files.AddMember("QGPL", "CHILD", "REPLACED");
        Assert.False(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Run("INSERT INTO QGPL/CHILD VALUES(1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
    }

    [Fact]
    public void Removing_a_self_referencing_member_keeps_the_file_constraints_for_replacement()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER REFERENCES QGPL/TREE)");
        Run("INSERT INTO QGPL/TREE VALUES(1,NULL),(2,1)");
        _files.RemoveMember("QGPL", "TREE", "TREE");
        Assert.True(_files.GetDefinition("QGPL", "TREE")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Single(_files.GetDefinition("QGPL", "TREE")!.SqlTable!.Constraints);
        _files.AddMember("QGPL", "TREE", "REPLACED");
        Run("INSERT INTO QGPL/TREE VALUES(1,NULL),(2,1)");
    }

    [Fact]
    public void Removing_a_parent_member_keeps_disabled_state_and_maintained_child_paths()
    {
        StatePair(); Assert.Null(State("CHILD", false).Error);
        _system.ObjectOperations.RemoveFileMember(new("QGPL", "PARENT"), "PARENT");
        var foreign = _files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.True(foreign.Defined); Assert.True(foreign.Disabled);
        Run("INSERT INTO QGPL/CHILD VALUES(2)"); _files.AddMember("QGPL", "PARENT", "REPLACED");
        foreign = _files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0];
        Assert.False(foreign.Defined); Assert.True(foreign.Disabled); Assert.True(foreign.CheckPending);
    }

    [Fact]
    public void Removing_a_cascading_parent_member_defines_cross_library_relations_without_deleting_children()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        _system.Libraries.CreateLibrary("OTHER");
        Run("CREATE TABLE OTHER/CHILD(ID INTEGER REFERENCES QGPL/PARENT ON DELETE CASCADE)");
        Run("INSERT INTO OTHER/CHILD VALUES(1)");
        _files.RemoveMember("QGPL", "PARENT", "PARENT");
        Assert.True(_files.GetDefinition("OTHER", "CHILD")!.SqlTable!.ForeignKeys![0].Defined);
        Assert.Equal(1, _files.RowCount("OTHER", "CHILD", "CHILD"));
    }
}
