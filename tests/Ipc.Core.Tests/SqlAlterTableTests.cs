using Ipc.Core.Messages;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlAlterTableTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,CODE VARCHAR(12) DEFAULT 'new',AMOUNT DECIMAL(29,2))");
        Run("INSERT INTO QGPL/DATA VALUES(1,'abcdef',10.25),(2,'xyz',20.75)");
    }
    public void Dispose() => _system.Dispose();
    private long Run(string sql) => _files.ExecuteSql(sql, Options);
    private IReadOnlyList<IReadOnlyList<object?>> Query(string sql)
    { using var cursor = _files.OpenSqlQuery(sql, Options); var result = new List<IReadOnlyList<object?>>(); while (cursor.Read()) result.Add(cursor.Current!.ToArray()); return result; }
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    [Fact]
    public void Adding_columns_preserves_sparse_relative_numbers_and_populates_defaults_in_declared_order()
    {
        Run("DELETE FROM QGPL/DATA WHERE ID=1");
        var identity = Scalar("SELECT id FROM sys_record_slots WHERE name='DATA'");
        Run("ALTER TABLE QGPL/DATA ADD COLUMN FLAG CHAR(2) NOT NULL DEFAULT 'Y' BEFORE CODE ADD EXTRA INTEGER NOT NULL");
        Assert.Equal(new[] { "ID", "FLAG", "CODE", "AMOUNT", "EXTRA" }, _files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Fields.Select(field => field.Name));
        var row = Assert.Single(Query("SELECT ID,FLAG,EXTRA FROM QGPL/DATA")); Assert.Equal(2L, row[0]); Assert.Equal("Y ", row[1]); Assert.Equal(0L, row[2]);
        Assert.Equal(identity, Scalar("SELECT id FROM sys_record_slots WHERE name='DATA'")); Assert.Equal(2L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        Assert.Equal(2L, Scalar("SELECT rowid FROM \"QGPL.DATA.DATA\""));
        Run("INSERT INTO QGPL/DATA(ID) VALUES(3)"); Assert.Equal(0L, Query("SELECT EXTRA FROM QGPL/DATA WHERE ID=3")[0][0]);
    }
    [Fact]
    public void Column_types_defaults_and_nullability_change_through_the_shared_record_layout()
    {
        Run("ALTER TABLE QGPL/DATA ALTER COLUMN CODE SET DATA TYPE VARCHAR(3) DEFAULT 'x' ALTER COLUMN AMOUNT SET DATA TYPE DECIMAL(29,1)");
        Assert.Equal(new[] { "abc", "xyz" }, Query("SELECT CODE FROM QGPL/DATA ORDER BY ID").Select(row => row[0]));
        Assert.Equal(new[] { "10.2", "20.7" }, Query("SELECT AMOUNT FROM QGPL/DATA ORDER BY ID").Select(row => row[0]!.ToString()));
        Run("ALTER TABLE QGPL/DATA ALTER AMOUNT SET DEFAULT 5 ALTER AMOUNT SET NOT NULL");
        Run("INSERT INTO QGPL/DATA(ID) VALUES(3)"); Assert.Equal("5.0", Query("SELECT AMOUNT FROM QGPL/DATA WHERE ID=3")[0][0]!.ToString());
        Run("ALTER TABLE QGPL/DATA ALTER AMOUNT DROP NOT NULL ALTER AMOUNT DROP DEFAULT");
        Run("INSERT INTO QGPL/DATA(ID) VALUES(4)"); Assert.Null(Query("SELECT AMOUNT FROM QGPL/DATA WHERE ID=4")[0][0]);
    }
    [Fact]
    public void Failed_conversion_rolls_back_all_actions_and_keeps_original_rows_and_schema()
    {
        var definition = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.Equal("SQL0406", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD FLAG INTEGER ALTER AMOUNT SET DATA TYPE DECIMAL(2,1)")).MessageId);
        Assert.Equal(definition, _files.GetDefinition("QGPL", "DATA")!.ToJson()); Assert.Equal("31.00", Query("SELECT SUM(AMOUNT) FROM QGPL/DATA")[0][0]!.ToString());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name LIKE 'ipc_alter_%'"));
    }
    [Fact]
    public void Adding_and_dropping_unique_and_primary_constraints_checks_existing_and_future_rows()
    {
        Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT IDKEY PRIMARY KEY(ID) ADD CONSTRAINT CODEKEY UNIQUE(CODE)");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(1,'different',1)")).MessageId);
        Run("ALTER TABLE QGPL/DATA DROP UNIQUE CODEKEY DROP PRIMARY KEY");
        Run("INSERT INTO QGPL/DATA VALUES(1,'abcdef',1)");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD UNIQUE(ID)")).MessageId);
        Assert.Empty(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Constraints);
    }
    [Fact]
    public void Existing_views_keep_frozen_star_columns_and_refresh_types_through_nested_projections()
    {
        Run("CREATE VIEW QGPL/V1 AS SELECT * FROM QGPL/DATA"); Run("CREATE VIEW QGPL/V2 AS SELECT CODE FROM QGPL/V1");
        Run("ALTER TABLE QGPL/DATA ADD FLAG INTEGER ALTER CODE SET DATA TYPE VARCHAR(5)");
        Assert.Equal(3, _files.GetDefinition("QGPL", "V1")!.PrimaryFormat.Fields.Count);
        Assert.Equal(5, _files.GetDefinition("QGPL", "V2")!.PrimaryFormat.Find("CODE")!.Length);
        Assert.Equal("abcde", Query("SELECT CODE FROM QGPL/V2 WHERE CODE='abcde'")[0][0]);
        Run("UPDATE QGPL/V2 SET CODE='view'"); Assert.Equal(2, Query("SELECT ID FROM QGPL/DATA WHERE CODE='view'").Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dropped_column_dependencies_follow_RESTRICT_or_cascade_all_view_descendants(bool cascade)
    {
        Run("CREATE VIEW QGPL/V1 AS SELECT CODE FROM QGPL/DATA"); Run("CREATE VIEW QGPL/V2 AS SELECT CODE FROM QGPL/V1");
        Run("CREATE VIEW QGPL/KEEP AS SELECT ID FROM QGPL/DATA"); Run("CREATE INDEX QGPL/BYCODE ON QGPL/DATA(CODE)");
        if (!cascade)
        {
            Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA DROP COLUMN CODE RESTRICT")).MessageId);
            Assert.True(_files.FileExists("QGPL", "V2")); Assert.NotNull(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Find("CODE"));
        }
        else
        {
            Run("ALTER TABLE QGPL/DATA DROP COLUMN CODE CASCADE");
            Assert.False(_files.FileExists("QGPL", "V1")); Assert.False(_files.FileExists("QGPL", "V2")); Assert.False(_files.FileExists("QGPL", "BYCODE"));
            Assert.Equal(2, Query("SELECT ID FROM QGPL/KEEP").Count);
        }
    }
}
