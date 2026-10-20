using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests
{
    private void StatePair(string rule = "CASCADE")
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER,CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT ON DELETE " + rule + ")");
        Run("INSERT INTO QGPL/CHILD VALUES(1)");
    }
    private SqlConstraintChange State(string file, bool enabled, string name = "REF", bool check = true)
        => Assert.Single(_files.ChangeConstraints("QGPL", file, [name], enabled, check));

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("RESTRICT")]
    [InlineData("NO ACTION")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public void Disabled_foreign_keys_keep_access_paths_and_allow_previously_restricted_IO(string rule)
    {
        StatePair(rule);
        var indexes = Scalar("SELECT group_concat(name,',') FROM (SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.CHILD.CHILD' ORDER BY name)");
        var changed = State("CHILD", false); Assert.True(changed.Changed); Assert.True(changed.Disabled); Assert.Null(changed.Error);
        Assert.Equal(indexes, Scalar("SELECT group_concat(name,',') FROM (SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name='QGPL.CHILD.CHILD' ORDER BY name)"));
        Run("INSERT INTO QGPL/CHILD VALUES(2)"); Run("UPDATE QGPL/PARENT SET ID=3"); Run("DELETE FROM QGPL/PARENT");
        Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD"));
        Assert.True(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys![0].Disabled);
        Assert.False(State("CHILD", false).Changed);
    }

    [Fact]
    public void Failed_foreign_enable_commits_pending_state_and_blocks_child_IO_and_parent_changes()
    {
        StatePair(); State("CHILD", false); Run("INSERT INTO QGPL/CHILD VALUES(2)");
        var pending = State("CHILD", true); Assert.True(pending.Changed); Assert.True(pending.CheckPending); Assert.False(pending.Disabled);
        Assert.Equal("CPF32B2", pending.Error!.MessageId);
        var repeated = State("CHILD", true); Assert.False(repeated.Changed); Assert.True(repeated.CheckPending); Assert.Equal("CPF32B2", repeated.Error!.MessageId);
        foreach (var sql in new[] { "INSERT INTO QGPL/CHILD VALUES(1)", "UPDATE QGPL/CHILD SET ID=1", "DELETE FROM QGPL/CHILD", "UPDATE QGPL/PARENT SET ID=1", "DELETE FROM QGPL/PARENT" })
        {
            var error = Assert.Throws<CpfException>(() => Run(sql)); Assert.Equal("SQL0971", error.MessageId); Assert.Equal("57011", error.SqlState);
        }
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "CHILD", "CHILD")).MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.OpenSqlQuery("SELECT ID FROM QGPL/CHILD", Options)).MessageId);
        Assert.Equal(1, _files.RowCount("QGPL", "PARENT", "PARENT")); Run("INSERT INTO QGPL/PARENT VALUES(2)");
        var disabled = State("CHILD", false, "*CHKPND"); Assert.True(disabled.Disabled); Assert.True(disabled.CheckPending);
        Assert.Equal(2, _files.RowCount("QGPL", "CHILD", "CHILD"));
        var enabled = State("CHILD", true, "*CHKPND"); Assert.False(enabled.CheckPending); Assert.Null(enabled.Error);
        Run("DELETE FROM QGPL/PARENT"); Assert.Equal(0, _files.RowCount("QGPL", "CHILD", "CHILD"));
    }

    [Fact]
    public void Pending_CHECK_blocks_reads_but_enforces_new_writes_and_supports_repair()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))");
        Assert.Null(State("DATA", false, "POS").Error); Run("INSERT INTO QGPL/DATA VALUES(-1),(NULL)");
        var pending = State("DATA", true, "POS"); Assert.True(pending.CheckPending); Assert.Equal("CPF32B2", pending.Error!.MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "DATA", "DATA")).MessageId);
        Run("INSERT INTO QGPL/DATA VALUES(2)");
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(-2)")).MessageId);
        Assert.Equal("SQL0971", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/DATA SET ID=1 WHERE ID<0")).MessageId);
        Run("UPDATE QGPL/DATA SET ID=1");
        Assert.Null(State("DATA", true, "POS").Error);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.False(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks![0].CheckPending);
    }

    [Fact]
    public void CLI_processes_constraint_names_sequentially_and_preserves_successful_changes()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0),CONSTRAINT SMALL CHECK(ID<10))");
        using var session = NativeForeignSession();
        var result = session.Execute("CHGPFCST FILE(QGPL/DATA) CST(POS MISSING SMALL) STATE(*DISABLED)");
        Assert.True(result.IsError); Assert.Equal("CPF32B2", result.MessageId); Assert.Contains("2 constraints changed", result.Message);
        Assert.All(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks!, item => Assert.True(item.Disabled));
        Run("INSERT INTO QGPL/DATA VALUES(-1)");
        result = session.Execute("chgpfcst QGPL/DATA *all state(*enabled)"); Assert.True(result.IsError);
        var checks = _files.GetDefinition("QGPL", "DATA")!.SqlTable!.Checks!;
        Assert.True(checks.Single(item => item.Name == "POS").CheckPending); Assert.False(checks.Single(item => item.Name == "SMALL").CheckPending);
        result = session.Execute("CHGPFCST QGPL/DATA *chkpnd STATE(*DISABLED)"); Assert.False(result.IsError, result.Message);
        Run("UPDATE QGPL/DATA SET ID=1");
        result = session.Execute("CHGPFCST QGPL/DATA *CHKPND STATE(*ENABLED)"); Assert.False(result.IsError, result.Message);
        Run("INSERT INTO QGPL/DATA VALUES(2)");
    }

    [Fact]
    public void CHECK_NO_skips_existing_row_validation_but_installs_enforcement_for_new_rows()
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))"); State("DATA", false, "POS"); Run("INSERT INTO QGPL/DATA VALUES(-1)");
        Assert.Null(State("DATA", true, "POS", check: false).Error);
        Assert.Equal(-1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.Equal("SQL0545", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(-2)")).MessageId);
    }

    [Theory]
    [InlineData("STATE(*BAD)")]
    [InlineData("CHECK(*BAD)")]
    [InlineData("CST(*ALL POS)")]
    public void Invalid_administration_options_leave_the_definition_unchanged(string option)
    {
        Run("CREATE TABLE QGPL/DATA(ID INTEGER,CONSTRAINT POS CHECK(ID>0))"); var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        using var session = NativeForeignSession();
        var result = session.Execute("CHGPFCST FILE(QGPL/DATA) " + (option.StartsWith("CST") ? option : "CST(POS) " + option));
        Assert.True(result.IsError); Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
    }

    [Fact]
    public void Constraint_states_round_trip_without_changing_legacy_JSON_or_constructor_shape()
    {
        StatePair(); var before = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        Assert.DoesNotContain("disabled", before); Assert.DoesNotContain("checkPending", before); Assert.DoesNotContain("defined", before);
        State("CHILD", false); var changed = _files.GetDefinition("QGPL", "CHILD")!.ToJson();
        Assert.True(FileDefinition.FromJson(changed).SqlTable!.ForeignKeys![0].Disabled);
        Assert.Null(State("CHILD", true).Error); Assert.Equal(before, _files.GetDefinition("QGPL", "CHILD")!.ToJson());
        var old = new SqlCheckConstraint("C", "ID>0"); var (_, condition, _, _, _, _) = old;
        Assert.Equal("ID>0", condition); Assert.False(old.Disabled); Assert.False(old.CheckPending);
    }
}
