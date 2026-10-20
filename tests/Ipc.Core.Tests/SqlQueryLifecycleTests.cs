using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryTests
{
    [Theory]
    [InlineData("SELECT ID FROM QGPL.DATA WHERE ID=?", "07001", -313)]
    [InlineData("SELECT BAD FROM QGPL.DATA", "42703", -206)]
    [InlineData("SELECT ID FROM QGPL.MISSING", "42704", -204)]
    [InlineData("SELECT ID FROM QGPL.DATA; DELETE FROM QGPL.DATA", "42601", -104)]
    [InlineData("SELECT 1/0 FROM QGPL.DATA", "22012", -802)]
    [InlineData("SELECT ID+2147483647 FROM QGPL.DATA", "22003", -802)]
    public void Query_diagnostics_expose_stable_SQLSTATE_and_SQLCODE(string sql, string state, int code)
    {
        Seed(); var error = Assert.Throws<CpfException>(() => Query(sql)); Assert.Equal(state, error.SqlState); Assert.Equal(code, error.SqlCode);
        Assert.DoesNotContain("ipc_q_", error.Message); Assert.DoesNotContain("QGPL.DATA.DATA", error.Message);
    }

    [Fact]
    public void Character_CAST_truncation_is_reported_as_SQL0445_with_SQLSTATE_01004()
    {
        Seed(); using var cursor = _files.OpenSqlQuery("SELECT CAST(NAME AS VARCHAR(2)) FROM QGPL.DATA ORDER BY ID", Options);
        Assert.True(cursor.Read()); Assert.Equal("Ad", cursor.Current![0]);
        var warning = Assert.Single(cursor.Diagnostics); Assert.Equal("01004", warning.SqlState); Assert.Equal(445, warning.SqlCode);
        while (cursor.Read()) { }
        Assert.NotEmpty(cursor.Diagnostics);
    }

    [Fact]
    public void A_query_reads_pending_record_unit_data_without_publishing_or_bypassing_it()
    {
        Seed(); _system.Journals.CreateReceiver(new("QGPL", "QRYRCV")); _system.Journals.Create(new("QGPL", "QRYJRN"), new("QGPL", "QRYRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "QRYJRN"), "*BOTH");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        {
            _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, new("QGPL", "DATA"));
            using (var change = _system.RecordTransactions.Open(job.Key))
            { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET AMOUNT='9.50' WHERE ID=1"); change.Complete(); }
            Assert.Equal(9.50m, Number(Assert.Single(Query("SELECT AMOUNT FROM QGPL.DATA WHERE ID=1").Rows)[0]));
        }
        Assert.Equal(1.25m, _files.ReadAll("QGPL", "DATA", "DATA")[0]["AMOUNT"]);
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        { _system.RecordTransactions.Rollback(job.Key); _system.RecordTransactions.End(job.Key); }
        Assert.Equal(1.25m, Number(Assert.Single(Query("SELECT AMOUNT FROM QGPL.DATA WHERE ID=1").Rows)[0]));
    }

    [Fact]
    public void Query_open_checks_file_read_authority_and_fetch_rechecks_profile_state()
    {
        Seed(); _system.Security.Profiles.Create(new() { Name = "QRYUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QRYUSER"), "QueryReaderFixture84");
        using (OperationIdentity.Enter("QRYUSER")) Assert.Equal("42501", Assert.Throws<CpfException>(() => Query("SELECT ID FROM QGPL.DATA")).SqlState);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "QRYUSER", Authorities.UseBits);
        SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QRYUSER")) { cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA ORDER BY ID", Options); Assert.True(cursor.Read()); }
        using (cursor)
        {
            var profile = _system.Security.Profiles.Get("QRYUSER"); profile.Status = ProfileStatus.Disabled; _system.Security.Profiles.Update(profile);
            using (OperationIdentity.Enter("QRYUSER")) Assert.Equal("42501", Assert.Throws<CpfException>(() => cursor.Read()).SqlState);
        }
    }

    [Fact]
    public void Query_cursors_cannot_be_fetched_by_another_execution_context()
    {
        Seed(); var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR"); SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options);
        using (cursor)
        using (OperationIdentity.Enter("QSECOFR", second.Key))
        { Assert.Equal("42501", Assert.Throws<CpfException>(() => cursor.Read()).SqlState); Assert.Null(cursor.Current); }
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_and_shutdown_close_the_reader_and_release_allocations(bool shutdown)
    {
        Seed(); using var cancel = new CancellationTokenSource(); var job = _system.Jobs.CreateInteractive("QSECOFR");
        using var identity = OperationIdentity.Enter("QSECOFR", job.Key);
        using var cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options, cancellationToken: cancel.Token);
        Assert.True(cursor.Read());
        if (shutdown) _system.Connections.Dispose(); else cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cursor.Read());
        if (!shutdown) Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("cells")]
    [InlineData("bytes")]
    [InlineData("work")]
    public void Query_resource_limits_fail_explicitly_and_close_partial_readers(string kind)
    {
        Seed(); var limits = kind switch {
            "rows" => new SqlQueryLimits(MaximumRows: 1), "cells" => new SqlQueryLimits(MaximumCells: 2),
            "bytes" => new SqlQueryLimits(MaximumBytes: 1), _ => new SqlQueryLimits(MaximumEvaluations: 1) };
        var error = Assert.Throws<CpfException>(() => {
            using var cursor = _files.OpenSqlQuery("SELECT ID,NAME FROM QGPL.DATA ORDER BY ID", Options, limits: limits);
            while (cursor.Read()) { }
        });
        Assert.Equal("57011", error.SqlState); Assert.Equal(4, Query("SELECT ID FROM QGPL.DATA").Rows.Count);
    }

    [Fact]
    public void A_forward_cursor_can_stream_many_rows_without_buffering_the_result_set()
    {
        Run("CREATE TABLE QGPL.DATA(ID INTEGER)");
        using (var connection = _system.Connections.Open()) using (var command = connection.CreateCommand())
        { command.CommandText = "WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<12000) INSERT INTO \"QGPL.DATA.DATA\"(ID) SELECT x FROM n"; command.ExecuteNonQuery(); }
        using var cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options);
        var count = 0; while (cursor.Read()) { count++; Assert.Equal((long)count, cursor.Current![0]); }
        Assert.Equal(12000, count); Assert.Null(cursor.Current);
    }

    [Fact]
    public void A_partially_consumed_query_keeps_its_object_allocation_until_disposed()
    {
        Seed(); var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR"); SqlQueryCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", first.Key)) { cursor = _files.OpenSqlQuery("SELECT ID FROM QGPL.DATA", Options); Assert.True(cursor.Read()); }
        var locks = new JobLockStore(_system.Connections);
        Assert.Throws<CpfException>(() => locks.Acquire(second.Key, new("QGPL", "DATA", ObjectType.File), JobLockMode.Exclusive, TimeSpan.Zero));
        cursor.Dispose(); using var held = locks.Acquire(second.Key, new("QGPL", "DATA", ObjectType.File), JobLockMode.Exclusive, TimeSpan.Zero);
    }

    [Fact]
    public void Numeric_and_character_function_failures_are_diagnosed_without_corrupting_storage()
    {
        Seed();
        Assert.Equal("22012", Assert.Throws<CpfException>(() => Query("SELECT AMOUNT/(ID-2) FROM QGPL.DATA ORDER BY ID")).SqlState);
        Assert.Equal("22011", Assert.Throws<CpfException>(() => Query("SELECT SUBSTR(NAME,0,3) FROM QGPL.DATA")).SqlState);
        Assert.Equal(4, _files.RowCount("QGPL", "DATA", "DATA"));
    }
}
