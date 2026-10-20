using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Dds;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests
{
    [Fact]
    public void A_logical_query_loads_only_its_bound_member_from_the_private_record_workspace()
    {
        var job = BeginQueryUnit();
        var source = string.Join('\n', new[] { LogicalFileTests.Line('R', "QUERYREC", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "ID"), LogicalFileTests.Line(' ', "DEPT"), LogicalFileTests.Line('S', "DEPT", "COMP(EQ 'A')") });
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("ACTIVE", source);
        _files.CreateLogicalFile("QGPL", "ACTIVE", definition, source);
        using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        using (var change = _system.RecordTransactions.Open(job.Key))
        { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET DEPT='B' WHERE ID=1"); change.Complete(); }
        Assert.Equal(2L, Assert.Single(Query("SELECT ID FROM QGPL.ACTIVE").Rows)[0]);
        _system.RecordTransactions.Rollback(job.Key);
        Assert.Equal(new long[] { 1,2 }, Query("SELECT ID FROM QGPL.ACTIVE ORDER BY ID").Rows.Select(row => (long)row[0]!));
        _system.RecordTransactions.End(job.Key);
    }

    [Fact]
    public void Logical_queries_check_the_physical_source_even_when_the_caller_can_read_the_logical_object()
    {
        Seed(); var source = LogicalFileTests.Line('R', "QUERYREC", "PFILE(QGPL/DATA)") + "\n" + LogicalFileTests.Line(' ', "ID");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("ACTIVE", source);
        _files.CreateLogicalFile("QGPL", "ACTIVE", definition, source);
        _system.Security.Profiles.Create(new() { Name = "QRYUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QRYUSER"), "QueryLogicalReader184");
        _system.Security.Authority.Grant("QGPL", "ACTIVE", ObjectType.File, "QRYUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QRYUSER")) Assert.Equal("42501", Assert.Throws<CpfException>(() => Query("SELECT ID FROM QGPL.ACTIVE")).SqlState);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QRYUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QRYUSER")) Assert.Equal(4, Query("SELECT ID FROM QGPL.ACTIVE").Rows.Count);
    }

    [Fact]
    public void The_job_query_cursor_budget_is_reusable_after_disposal()
    {
        Seed(); var job = _system.Jobs.CreateInteractive("QSECOFR"); using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        var cursors = new List<SqlQueryCursor>();
        try
        {
            for (var index = 0; index < 128; index++) cursors.Add(_files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options));
            Assert.Equal("57011", Assert.Throws<CpfException>(() => _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options)).SqlState);
            cursors[0].Dispose(); using var replacement = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options); Assert.True(replacement.Read());
        }
        finally { foreach (var cursor in cursors) cursor.Dispose(); }
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Theory]
    [InlineData("SELECT NAME LIKE '%' ESCAPE 'ab' FROM QGPL.DATA", "22019")]
    [InlineData("SELECT NAME LIKE 'x!' ESCAPE '!' FROM QGPL.DATA", "22025")]
    [InlineData("SELECT ID FROM QGPL.DATA FETCH FIRST ? ROWS ONLY", "22023")]
    public void Invalid_escape_and_fetch_operands_have_reason_specific_states(string sql, string state)
    {
        Seed(); var error = Assert.Throws<CpfException>(() => Query(sql, sql.Contains('?') ? [new SqlQueryParameter(-1)] : [])); Assert.Equal(state, error.SqlState);
    }

    [Fact]
    public async Task Cancelling_an_executing_cross_join_unwinds_SQLite_and_keeps_the_catalog_usable()
    {
        Seed(); using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var sql = "SELECT COUNT(*) FROM " + string.Join(',', Enumerable.Range(0, 16).Select(index => "QGPL.DATA A" + index));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => { using var cursor = _files.OpenSqlQuery(sql, Options, cancellationToken: cancellation.Token); cursor.Read(); })).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, Query("SELECT ID FROM QGPL.DATA").Rows.Count);
    }

    [Fact]
    public void SQL_and_system_naming_use_the_same_resolved_file_metadata()
    {
        Seed(); using var cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL/DATA WHERE ID=1", Options with { Naming = SqlNaming.System });
        Assert.True(cursor.Read()); Assert.Equal(1L, cursor.Current![0]); Assert.False(cursor.Read());
    }
    [Fact]
    public void Revoking_file_read_authority_closes_an_already_open_query_on_its_next_fetch()
    {
        Seed(); _system.Security.Profiles.Create(new() { Name = "QRYUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QRYUSER"), "QueryAuthorityReader98");
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QRYUSER", Authorities.UseBits);
        SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QRYUSER")) { cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options); Assert.True(cursor.Read()); }
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QRYUSER", AuthorityBit.None);
        using (cursor)
        using (OperationIdentity.Enter("QRYUSER")) Assert.Equal("42501", Assert.Throws<CpfException>(() => cursor.Read()).SqlState);
        Assert.Null(cursor.Current);
    }

}
