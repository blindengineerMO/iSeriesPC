using Ipc.Core.Messages;
using Ipc.Db.Dds;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    private void AddCursorRows()
    {
        for (var id = 2; id <= 4; id++)
            _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = (long)id, ["TEXT"] = "row " + id });
    }
    private static long ReadId(DatabaseRecordCursor cursor) => (long)cursor.Read()!["ID"]!;
    [Fact]
    public void Rollback_before_the_first_commit_restores_the_cursor_open_position()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); Assert.Equal(2, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(1, ReadId(cursor));
    }
    [Fact]
    public void Commit_keeps_position_and_rollback_returns_to_the_last_commit_even_without_record_changes()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(2, ReadId(cursor)); Assert.Equal(3, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
    }
    [Fact]
    public void Successful_record_publication_also_establishes_the_cursor_boundary()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); Change("SECOND", "committed with cursor");
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal(2, ReadId(cursor));
        Change("SECOND", "discarded"); _system.RecordTransactions.Rollback(_job.Key);
        Assert.Equal(2, ReadId(cursor)); Assert.Equal("committed with cursor", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void Rollback_clears_an_EOF_reached_after_the_committed_position()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key);
        while (cursor.Read() is not null) { }
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
    }
    [Fact]
    public void An_EOF_committed_as_the_boundary_is_restored_as_EOF()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); Assert.Null(cursor.Read()); _system.RecordTransactions.Commit(_job.Key);
        _system.RecordTransactions.Rollback(_job.Key); Assert.Null(cursor.Read());
    }
    [Fact]
    public void A_failed_commit_does_not_replace_the_previous_cursor_boundary()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key); Assert.Equal(2, ReadId(cursor));
        Change("SECOND", "pending");
        Sql("CREATE TRIGGER fail_cursor_boundary BEFORE INSERT ON sys_journal_entries WHEN NEW.code='C' AND NEW.entry_type='CM' BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<SqliteException>(() => _system.RecordTransactions.Commit(_job.Key));
        Assert.Equal(3, ReadId(cursor)); _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void A_cancelled_commit_keeps_the_prior_cursor_boundary()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key); Assert.Equal(2, ReadId(cursor));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _system.RecordTransactions.Commit(_job.Key, cancellation.Token));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
    }
    [Fact]
    public void Multiple_cursors_restore_their_individual_positions_at_one_job_boundary()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var first = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); using var second = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal(1, ReadId(first)); Assert.Equal(1, ReadId(second)); Assert.Equal(2, ReadId(second));
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal(2, ReadId(first)); Assert.Equal(3, ReadId(second));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(first)); Assert.Equal(3, ReadId(second));
    }
    [Fact]
    public void Closing_a_cursor_does_not_reopen_it_and_a_new_cursor_starts_at_its_own_open_position()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var old = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(old)); _system.RecordTransactions.Commit(_job.Key); old.Dispose();
        using var current = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(current));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(1, ReadId(current)); Assert.Throws<ObjectDisposedException>(() => old.Read());
    }
    [Fact]
    public void A_record_definition_cannot_end_until_its_commitment_cursors_close()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Throws<CpfException>(() => _system.RecordTransactions.End(_job.Key));
        cursor.Dispose(); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Ordinary_unenlisted_cursors_are_not_repositioned_or_attached_to_the_record_definition()
    {
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, First);
        using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "SECOND", "SECOND"); Assert.Equal(1, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Null(cursor.Read()); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void An_existing_cursor_joins_on_its_first_read_after_the_file_is_enlisted()
    {
        AddCursorRows(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(cursor));
        Begin(); Assert.Equal(2, ReadId(cursor)); Assert.Equal(3, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
        Assert.Throws<CpfException>(() => _system.RecordTransactions.End(_job.Key));
    }
    [Fact]
    public void Other_jobs_cannot_read_or_close_an_owned_commitment_cursor()
    {
        Begin(); var other = _system.Jobs.CreateInteractive("QSECOFR"); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        { Assert.Throws<CpfException>(() => cursor.Read()); Assert.Throws<CpfException>(() => cursor.Dispose()); }
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) { Assert.Equal(1, ReadId(cursor)); cursor.Dispose(); }
    }
    [Fact]
    public void Job_end_invalidates_cursor_reads_but_allows_disposal()
    {
        Begin(); DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        _system.Jobs.Complete(_job.Key);
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        { Assert.Throws<CpfException>(() => cursor.Read()); cursor.Dispose(); }
    }
    [Fact]
    public void Reopened_services_share_the_same_cursor_boundary_registry()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(cursor));
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); reopened.RecordTransactions.Commit(_job.Key);
        Assert.Equal(2, ReadId(cursor)); reopened.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
    }
    [Fact]
    public void Keyset_checkpoints_survive_other_jobs_inserting_before_the_position()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key);
        Sql("INSERT INTO \"QGPL.FIRST.FIRST\" VALUES(0,'inserted before position')");
        Assert.Equal(2, ReadId(cursor)); _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(2, ReadId(cursor));
    }
    [Fact]
    public void Physical_RRN_checkpoints_survive_deleting_an_earlier_record()
    {
        AddCursorRows(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", keyed: false);
        Assert.Equal(1, ReadId(cursor)); Assert.Equal(2, ReadId(cursor)); _system.RecordTransactions.Commit(_job.Key);
        Sql("DELETE FROM \"QGPL.FIRST.FIRST\" WHERE ID=1"); Assert.Equal(3, ReadId(cursor));
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(3, ReadId(cursor));
    }
    [Fact]
    public void Logical_member_and_RRN_ties_remain_distinct_at_a_commitment_boundary()
    {
        _files.AddMember("QGPL", "FIRST", "ALT");
        _files.Insert("QGPL", "FIRST", "ALT", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "alternate member" });
        var source = LogicalFileTests.Line('R', "CURREC", "PFILE(QGPL/FIRST)") + "\n" + LogicalFileTests.Line('K', "ID");
        var definition = new LogicalDdsCompiler(_ => (First, _files.GetDefinition("QGPL", "FIRST")!)).Compile("CURVIEW", source);
        _files.CreateLogicalFile("QGPL", "CURVIEW", definition, source, ["FIRST", "ALT"]); Begin();
        using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "CURVIEW", "CURVIEW"); Assert.Equal("original", cursor.Read()!["TEXT"]);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("alternate member", cursor.Read()!["TEXT"]); Assert.Null(cursor.Read());
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal("alternate member", cursor.Read()!["TEXT"]);
    }
    [Fact]
    public void A_waiting_cursor_read_can_be_cancelled_without_crossing_the_active_statement_gate()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key); using var cancellation = new CancellationTokenSource();
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", cancellationToken: cancellation.Token);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            using var started = new ManualResetEventSlim();
            var read = Task.Run(() => { started.Set(); return cursor.Read(); }); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => read.GetAwaiter().GetResult());
        }
        _system.RecordTransactions.Rollback(_job.Key);
    }
    [Fact]
    public async Task Concurrent_reads_of_one_commitment_cursor_do_not_repeat_or_skip_records()
    {
        for (var id = 2; id <= 40; id++)
            _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = (long)id, ["TEXT"] = "row " + id });
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        // This checks serialization, not throughput under the full suite's load.
        // Cancel the reads themselves on timeout so workers release the unit gate.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", cancellationToken: budget.Token);
        var values = new global::System.Collections.Concurrent.ConcurrentBag<long>(); using var start = new ManualResetEventSlim();
        Task Reader() => Task.Run(() => {
            Assert.True(start.Wait(TimeSpan.FromSeconds(5)));
            while (cursor.Read() is { } row) values.Add((long)row["ID"]!);
        });
        var first = Reader(); var second = Reader(); start.Set(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(Enumerable.Range(1, 40).Select(value => (long)value), values.Order());
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(1, ReadId(cursor));
    }
    [Fact]
    public void Commitment_cursor_capacity_is_reusable_after_close()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key); var cursors = new List<DatabaseRecordCursor>();
        try
        {
            for (var index = 0; index < 256; index++) cursors.Add(_files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"));
            Assert.Throws<CpfException>(() => _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"));
            cursors[0].Dispose(); cursors[0] = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.Equal(1, ReadId(cursors[0]));
        }
        finally { foreach (var cursor in cursors) cursor.Dispose(); }
        _system.RecordTransactions.End(_job.Key);
    }
}
