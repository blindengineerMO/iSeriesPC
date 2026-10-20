using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed partial class SqlForeignKeyTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlForeignKeyTests() { _system.Start(); _files = new(_system.Connections, _system.Objects); }
    public void Dispose() => _system.Dispose();
    private long Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    [Fact]
    public void Named_and_inline_keys_enforce_inserts_updates_and_null_exemption()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER CONSTRAINT REF REFERENCES QGPL/PARENT)");
        Run("INSERT INTO QGPL/CHILD VALUES(1),(NULL)");
        var error = Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")); Assert.Equal("SQL0530", error.MessageId);
        Assert.Equal("23503", error.SqlState);
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/CHILD SET ID=2 WHERE ID=1")).MessageId);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Composite_keys_match_exact_decimal_and_padded_character_values_in_parent_order()
    {
        Run("CREATE TABLE QGPL/PARENT(A DECIMAL(29,2),B CHAR(4),UNIQUE(A,B))");
        Run("INSERT INTO QGPL/PARENT VALUES(10000000000000000000000000.01,'A')");
        Run("CREATE TABLE QGPL/CHILD(X CHAR(4),Y DECIMAL(29,2),FOREIGN KEY(X,Y) REFERENCES QGPL/PARENT(B,A))");
        Run("INSERT INTO QGPL/CHILD VALUES('A   ',10000000000000000000000000.01),('Z',NULL)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES('A',10000000000000000000000000.00)")).MessageId);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
    }
    [Theory]
    [InlineData("CASCADE", 0L, null)]
    [InlineData("SET NULL", 1L, null)]
    [InlineData("SET DEFAULT", 1L, 0L)]
    public void Delete_actions_update_dependents_in_the_same_atomic_statement(string rule, long rows, long? value)
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("INSERT INTO QGPL/PARENT VALUES(0),(1)");
        Run("CREATE TABLE QGPL/CHILD(ID INTEGER DEFAULT 0,FOREIGN KEY(ID) REFERENCES QGPL/PARENT ON DELETE " + rule + ")");
        Run("INSERT INTO QGPL/CHILD VALUES(1)"); Run("DELETE FROM QGPL/PARENT WHERE ID=1");
        Assert.Equal(rows, Scalar("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        if (rows != 0) Assert.Equal((object?)value ?? DBNull.Value, Scalar("SELECT ID FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void SET_NULL_changes_only_nullable_components_of_a_composite_key()
    {
        Run("CREATE TABLE QGPL/PARENT(A INTEGER,B INTEGER,PRIMARY KEY(A,B))"); Run("INSERT INTO QGPL/PARENT VALUES(1,2)");
        Run("CREATE TABLE QGPL/CHILD(A INTEGER NOT NULL,B INTEGER,FOREIGN KEY(A,B) REFERENCES QGPL/PARENT ON DELETE SET NULL)");
        Run("INSERT INTO QGPL/CHILD VALUES(1,2)"); Run("DELETE FROM QGPL/PARENT");
        Assert.Equal(1L, Scalar("SELECT A FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(DBNull.Value, Scalar("SELECT B FROM \"QGPL.CHILD.CHILD\""));
    }
    [Fact]
    public void Self_referencing_inserts_validate_the_complete_statement_and_delete_allows_internal_dependents()
    {
        Run("CREATE TABLE QGPL/TREE(ID INTEGER PRIMARY KEY,PARENT INTEGER REFERENCES QGPL/TREE)");
        Run("INSERT INTO QGPL/TREE VALUES(1,2),(2,NULL)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/TREE VALUES(3,4),(4,5)")).MessageId);
        Assert.Equal(2L, Scalar("SELECT count(*) FROM \"QGPL.TREE.TREE\"")); Run("DELETE FROM QGPL/TREE");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM \"QGPL.TREE.TREE\""));
    }
    [Theory]
    [InlineData("ID BIGINT REFERENCES QGPL/PARENT", "SQL0538")]
    [InlineData("ID INTEGER REFERENCES QGPL/PARENT(MISSING)", "SQL0206")]
    [InlineData("ID INTEGER NOT NULL REFERENCES QGPL/PARENT ON DELETE SET NULL", "SQL0629")]
    [InlineData("ID INTEGER REFERENCES QGPL/MISSING", "SQL0204")]
    public void Invalid_definitions_publish_no_child(string fields, string expected)
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)");
        Assert.Equal(expected, Assert.Throws<CpfException>(() => Run("CREATE TABLE QGPL/CHILD(" + fields + ")")).MessageId);
        Assert.False(_files.FileExists("QGPL", "CHILD"));
    }
    [Fact]
    public void ALTER_validates_existing_rows_and_drop_removes_enforcement()
    {
        Run("CREATE TABLE QGPL/PARENT(ID INTEGER PRIMARY KEY)"); Run("CREATE TABLE QGPL/CHILD(ID INTEGER)"); Run("INSERT INTO QGPL/CHILD VALUES(1)");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT")).MessageId);
        Assert.Null(_files.GetDefinition("QGPL", "CHILD")!.SqlTable!.ForeignKeys);
        Run("INSERT INTO QGPL/PARENT VALUES(1)"); Run("ALTER TABLE QGPL/CHILD ADD CONSTRAINT REF FOREIGN KEY(ID) REFERENCES QGPL/PARENT");
        Assert.Equal("SQL0530", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/CHILD VALUES(2)")).MessageId);
        Run("ALTER TABLE QGPL/CHILD DROP FOREIGN KEY REF"); Run("INSERT INTO QGPL/CHILD VALUES(2)");
    }
}
