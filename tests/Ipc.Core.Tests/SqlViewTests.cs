using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class SqlViewTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    private static SqlExecutionOptions Options => new(SqlNaming.System, "QGPL", "QSECOFR", ["QGPL"]);
    public SqlViewTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SqlViewFixture128");
        _files = new(_system.Connections, _system.Objects);
        Run("CREATE TABLE QGPL/DATA(ID INTEGER NOT NULL,CODE VARCHAR(10),AMOUNT DECIMAL(29,2) NOT NULL)");
        Run("INSERT INTO QGPL/DATA VALUES(2,'b',100),(1,'a',2),(3,NULL,10)");
        _session = new(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }
    private long Run(string sql) => _files.ExecuteSql(sql, Options);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private IReadOnlyList<IReadOnlyList<object?>> Query(string sql)
    { using var cursor = _files.OpenSqlQuery(sql, Options); var rows = new List<IReadOnlyList<object?>>(); while (cursor.Read()) rows.Add(cursor.Current!.ToArray()); return rows; }
    [Fact]
    public void Views_are_ordinary_nonkeyed_catalog_files_with_live_typed_query_results()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CODE,AMOUNT*2 AS TOTAL FROM QGPL/DATA WHERE ID<3 RCDFMT RPTREC");
        var descriptor = _system.Objects.GetRequired("QGPL", "REPORT", ObjectType.File); Assert.Equal("*LF", descriptor.Attribute); Assert.Equal("RPTREC", descriptor.Format);
        var definition = _files.GetDefinition("QGPL", "REPORT")!; Assert.NotNull(definition.SqlView); Assert.Null(definition.Logical);
        var rows = Query("SELECT ID,CODE,TOTAL FROM QGPL/REPORT ORDER BY ID"); Assert.Equal(2, rows.Count); Assert.Equal(1L, rows[0][0]); Assert.Equal("4.00", rows[0][2]!.ToString());
        Run("UPDATE QGPL/DATA SET AMOUNT=7 WHERE ID=1"); Assert.Equal("14.00", Query("SELECT TOTAL FROM QGPL/REPORT WHERE ID=1")[0][0]!.ToString());
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_object_dependencies WHERE source_name='REPORT' AND target_name='DATA'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.REPORT.REPORT'"));
    }
    [Fact]
    public void Nested_joined_and_grouped_views_share_exact_query_semantics()
    {
        Run("CREATE VIEW QGPL/BASE AS SELECT ID,CODE,AMOUNT FROM QGPL/DATA WHERE ID>1");
        Run("CREATE VIEW QGPL/JOINED AS SELECT A.ID,A.AMOUNT+B.AMOUNT AS TOTAL FROM QGPL/BASE A JOIN QGPL/DATA B ON A.ID=B.ID");
        Run("CREATE VIEW QGPL/GROUPED(N,TOTAL) AS SELECT COUNT(*),SUM(TOTAL) FROM QGPL/JOINED");
        var row = Assert.Single(Query("SELECT N,TOTAL FROM QGPL/GROUPED")); Assert.Equal(2L, row[0]); Assert.Equal("220.00", row[1]!.ToString());
        Run("DROP VIEW QGPL/BASE"); Assert.False(_files.FileExists("QGPL", "BASE")); Assert.False(_files.FileExists("QGPL", "JOINED")); Assert.False(_files.FileExists("QGPL", "GROUPED"));
        Assert.True(_files.FileExists("QGPL", "DATA"));
    }
    [Fact]
    public void View_READ_delegates_to_its_owner_without_granting_the_reader_base_READ()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CODE FROM QGPL/DATA WHERE ID=1");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "SqlViewReader834");
        _system.Security.Authority.Grant("QGPL", "REPORT", ObjectType.File, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QUSER", AuthorityBit.None);
        using var reader = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QUSER"), CancellationToken.None);
        using var identity = OperationIdentity.Enter("QUSER", reader.Job.Key);
        Assert.Equal(1L, Assert.Single(Query("SELECT ID FROM QGPL/REPORT"))[0]);
        Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Query("SELECT ID FROM QGPL/DATA")).MessageId);
        Assert.Equal("QUSER", OperationIdentity.Current!.Principal); Assert.Equal(reader.Job.Key, OperationIdentity.Current.Job);
    }
    [Fact]
    public void Compatible_replacement_preserves_authority_and_invalid_dependent_replacement_rolls_back()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT ID,CODE FROM QGPL/DATA"); Run("CREATE VIEW QGPL/CHILD AS SELECT ID FROM QGPL/REPORT");
        _system.Security.Authority.Grant("QGPL", "REPORT", ObjectType.File, "QUSER", Authorities.UseBits);
        Run("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT ID,CODE FROM QGPL/DATA WHERE ID=1");
        Assert.Single(Query("SELECT ID FROM QGPL/CHILD")); Assert.Equal((long)(int)Authorities.UseBits, Scalar("SELECT bits FROM sys_authorities WHERE name='REPORT' AND holder='QUSER'"));
        Assert.Throws<CpfException>(() => Run("CREATE OR REPLACE VIEW QGPL/REPORT AS SELECT CODE FROM QGPL/DATA"));
        Assert.Equal(1L, Assert.Single(Query("SELECT ID FROM QGPL/CHILD"))[0]);
    }
}
