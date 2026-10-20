using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewMutationTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlViewMutationTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ViewMutationFixture542");
        _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,AMOUNT DECIMAL(29,2) NOT NULL DEFAULT 5,CODE VARCHAR(10) DEFAULT 'hidden')");
        Run("INSERT INTO QGPL/DATA VALUES(1,10,'one'),(2,20,'two'),(3,30,'three')");
        _session = new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }
    private long Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private IReadOnlyList<IReadOnlyList<object?>> Query(string sql)
    { using var cursor = _files.OpenSqlQuery(sql, Options); var rows = new List<IReadOnlyList<object?>>(); while (cursor.Read()) rows.Add(cursor.Current!.ToArray()); return rows; }
    [Fact]
    public void Insert_update_and_delete_map_aliases_to_the_base_and_keep_hidden_defaults()
    {
        Run("CREATE VIEW QGPL/REPORT(KEY,VALUE) AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<3");
        Assert.Equal(1, Run("INSERT INTO QGPL/REPORT(KEY) VALUES(4)"));
        Assert.Equal("hidden", Query("SELECT CODE FROM QGPL/DATA WHERE ID=4")[0][0]); Assert.Empty(Query("SELECT KEY FROM QGPL/REPORT WHERE KEY=4"));
        Assert.Equal(2, Run("UPDATE QGPL/REPORT SET VALUE=VALUE+1")); Assert.Equal("32.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA WHERE ID<3")[0][0]!.ToString());
        Assert.Equal(1, Run("DELETE FROM QGPL/REPORT WHERE KEY=1")); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Derived_columns_are_readable_in_predicates_but_not_updatable_targets()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT*2 AS TOTAL FROM QGPL/DATA");
        Assert.Equal(1, Run("UPDATE QGPL/REPORT SET ID=4 WHERE TOTAL=20"));
        Assert.Equal("SQL0151", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET TOTAL=99")).MessageId);
        Assert.Equal("SQL0151", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(5,100)")).MessageId);
        Assert.Equal(1, Run("INSERT INTO QGPL/REPORT(ID) VALUES(5)"));
        Assert.Equal(1, Run("DELETE FROM QGPL/REPORT WHERE TOTAL=60"));
    }
    [Fact]
    public void CHECK_OPTION_checks_normalized_insert_and_update_rows_and_rolls_back_the_statement()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,AMOUNT FROM QGPL/DATA WHERE ID<3 WITH CHECK OPTION RCDFMT CHECKREC");
        var insert = Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/REPORT VALUES(0,1),(4,2)")); Assert.Equal("SQL0161", insert.MessageId); Assert.Equal("44000", insert.SqlState);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("UPDATE QGPL/REPORT SET ID=ID+2")).MessageId);
        Assert.Equal(new long[] { 1, 2, 3 }, Query("SELECT ID FROM QGPL/DATA ORDER BY ID").Select(row => (long)row[0]!));
    }
    [Theory]
    [InlineData("LOCAL", true)]
    [InlineData("CASCADED", false)]
    public void Local_and_cascaded_checks_follow_the_IBM_nested_view_boundary(string option, bool accepted)
    {
        Run("CREATE VIEW QGPL/V1 AS SELECT ID FROM QGPL/DATA WHERE ID>10");
        Run("CREATE VIEW QGPL/V2 AS SELECT ID FROM QGPL/V1 WHERE ID<100 WITH " + option + " CHECK OPTION");
        Run("CREATE VIEW QGPL/V3 AS SELECT ID FROM QGPL/V2 WHERE ID>20");
        if (accepted) Assert.Equal(1, Run("INSERT INTO QGPL/V3 VALUES(5)"));
        else Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/V3 VALUES(5)")).MessageId);
        Assert.Equal(1, Run("INSERT INTO QGPL/V3 VALUES(15)")); Assert.Empty(Query("SELECT ID FROM QGPL/V3"));
        Assert.Equal("SQL0161", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/V3 VALUES(200)")).MessageId);
    }
}
