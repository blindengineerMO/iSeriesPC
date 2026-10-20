using Ipc.Core.Messages;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    [Theory]
    [InlineData("NO ACTION","23504")]
    [InlineData("RESTRICT","23001")]
    public void Parent_update_and_delete_failures_preserve_rows_and_native_diagnostics(string rule,string state)
    {
        Pair("ON DELETE " + rule + " ON UPDATE " + rule);
        var update = Assert.Throws<CpfException>(() => Run("UPDATE QGPL/PARENT SET ID=2"));
        Assert.Equal("SQL0531",update.MessageId); Assert.Equal(state,update.SqlState);
        var delete = Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT"));
        Assert.Equal("SQL0532",delete.MessageId); Assert.Equal(state,delete.SqlState);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\""));
        Assert.Equal(1,Run("UPDATE QGPL/PARENT SET ID=ID"));
        Assert.Equal(0,Run("DELETE FROM QGPL/PARENT WHERE ID=99"));
    }
    [Fact]
    public void NO_ACTION_checks_parent_key_swaps_at_statement_end()
    {
        Pair(); Run("INSERT INTO QGPL/PARENT VALUES(2)"); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        Assert.Equal(2,Run("UPDATE QGPL/PARENT SET ID=3-ID"));
        Assert.Equal(2L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\" WHERE rowid=1"));
        Assert.Equal(2L,Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void RESTRICT_rejects_parent_key_swaps_before_changing_a_referenced_key()
    {
        Pair("ON UPDATE RESTRICT"); Run("INSERT INTO QGPL/PARENT VALUES(2)");
        var error = Assert.Throws<CpfException>(() => Run("UPDATE QGPL/PARENT SET ID=3-ID"));
        Assert.Equal("SQL0531",error.MessageId); Assert.Equal("23001",error.SqlState);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\" WHERE rowid=1"));
    }
    [Fact]
    public void SET_DEFAULT_requires_a_matching_default_parent_and_rolls_back_the_whole_delete()
    {
        Pair("ON DELETE SET DEFAULT");
        Run("ALTER TABLE QGPL/CHILD ALTER ID SET DEFAULT 0");
        var error = Assert.Throws<CpfException>(() => Run("DELETE FROM QGPL/PARENT"));
        Assert.Equal("SQL0530",error.MessageId); Assert.Equal("23503",error.SqlState);
        Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(1L,Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void ALTER_of_a_referenced_type_cannot_silently_change_the_constraint_contract()
    {
        Pair(); var before = _files.GetDefinition("QGPL","PARENT")!.ToJson();
        Assert.Equal("SQL0190",Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/PARENT ALTER ID SET DATA TYPE BIGINT")).MessageId);
        Assert.Equal(before,_files.GetDefinition("QGPL","PARENT")!.ToJson());
        Assert.Equal("SQL0530",Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
    }
}
