using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    private void AddDuplicateRecord() => _files.Insert("QGPL", "FIRST", "FIRST", "REC",
        new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "duplicate" });
    private static Dictionary<string, object?> PointText(string text) => new() { ["TEXT"] = text };
    private static Dictionary<string, object?> PointKey(long id) => new() { ["ID"] = id };

    [Fact]
    public void Current_record_writes_stage_one_duplicate_at_a_time_and_publish_exact_RRNs()
    {
        AddDuplicateRecord(); var other = _system.Jobs.CreateInteractive("QSECOFR");
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.Equal("original", cursor.Read()!["TEXT"]); cursor.Update(PointText("first updated"));
        Assert.Equal("duplicate", cursor.Read()!["TEXT"]); cursor.Delete();
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.Equal(2L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE rowid=1"));
        var locks = new JobLockStore(_system.Connections);
        using (OperationIdentity.Enter("QSECOFR", other.Key))
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => locks.Acquire(other.Key, new("QGPL", "FIRST", ObjectType.File, "FIRST", 1), JobLockMode.Exclusive, TimeSpan.Zero)).MessageId);
        _system.RecordTransactions.Commit(_job.Key);
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        using (locks.Acquire(other.Key, new("QGPL", "FIRST", ObjectType.File, "FIRST", 1), JobLockMode.Exclusive, TimeSpan.Zero)) { }
        Assert.Equal("first updated", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(1L, Sql("SELECT record_number FROM sys_journal_entries WHERE entry_type='UP' ORDER BY sequence DESC LIMIT 1"));
        Assert.Equal(2L, Sql("SELECT record_number FROM sys_journal_entries WHERE entry_type='DL' ORDER BY sequence DESC LIMIT 1"));
    }
    [Fact]
    public void Rollback_discards_current_record_writes_and_restores_the_committed_position()
    {
        AddDuplicateRecord(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST");
        Assert.NotNull(cursor.Read()); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("duplicate", cursor.Read()!["TEXT"]); cursor.Update(PointText("discarded"));
        Assert.Null(cursor.Current); _system.RecordTransactions.Rollback(_job.Key);
        Assert.Throws<CpfException>(() => cursor.Delete());
        Assert.Equal("duplicate", cursor.Read()!["TEXT"]); cursor.Delete();
        _system.RecordTransactions.Rollback(_job.Key);
        Assert.Equal("duplicate", cursor.Read()!["TEXT"]); Assert.Equal(2L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_record_writes_can_target_an_insert_visible_only_in_the_pending_unit(bool delete)
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "pending insert" });
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.True(cursor.Chain(PointKey(2)));
        if (delete) cursor.Delete(); else cursor.Update(PointText("updated insert"));
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(delete ? 1L : 2L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        if (!delete) Assert.Equal("updated insert", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE ID=2"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiled_RPG_changes_only_the_retrieved_duplicate_under_commitment_control(bool delete)
    {
        AddDuplicateRecord(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var operation = delete ? "delete FIRST;" : "eval TEXT = 'RPG updated';\nupdate FIRST;";
        var source = "**free\ndcl-s ID int(20);\ndcl-s TEXT char(100);\nread FIRST;\nread FIRST;\n" + operation + "\nreturn;\n";
        var result = new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" })
            .Run(RpgCompiler.Compile("POINTWRITE", "QGPL", source));
        Assert.Equal(1m, RpgValues.ToDecimal(result.ReadValue("ID")));
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE rowid=1"));
        Assert.Equal(delete ? null : "RPG updated", (Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE rowid=2") as string)?.TrimEnd());
    }
    [Fact]
    public void Compiled_RPG_can_change_a_key_without_changing_the_duplicate_sibling()
    {
        AddDuplicateRecord();
        var source = "**free\ndcl-s ID int(20);\ndcl-s TEXT char(100);\nread FIRST;\nread FIRST;\neval ID = 9;\nupdate FIRST;\nreturn;\n";
        new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL" })
            .Run(RpgCompiler.Compile("POINTKEY", "QGPL", source));
        Assert.Equal(1L, Sql("SELECT ID FROM \"QGPL.FIRST.FIRST\" WHERE rowid=1"));
        Assert.Equal(9L, Sql("SELECT ID FROM \"QGPL.FIRST.FIRST\" WHERE rowid=2"));
    }
    [Fact]
    public void Current_record_trigger_effects_are_staged_and_not_repeated_during_publication()
    {
        AddDuplicateRecord();
        Sql("CREATE TRIGGER point_effect AFTER UPDATE ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=TEXT||'x'; END");
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read()); cursor.Update(PointText("once"));
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\"")); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("originalx", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
        Assert.Equal("duplicate", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE rowid=2"));
    }
    [Fact]
    public void An_external_change_after_a_pending_read_is_rejected_without_an_intent()
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST"); Assert.NotNull(cursor.Read());
        Sql("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='external'");
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Update(PointText("stale"))).MessageId);
        Assert.Equal(0, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal("external", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
        Assert.True(cursor.Chain(PointKey(1))); cursor.Update(PointText("fresh")); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("fresh", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public async Task Cancelling_a_current_record_writer_releases_its_lease_without_changing_the_record()
    {
        using var stop = new CancellationTokenSource();
        using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", cancellationToken: stop.Token);
        Assert.NotNull(cursor.Read());
        using (var blocker = _system.Connections.Open())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            stop.CancelAfter(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => cursor.Update(PointText("cancelled"))));
        }
        Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.NotNull(cursor.Current);
        var other = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", other.Key))
        using (new JobLockStore(_system.Connections).Acquire(other.Key, new("QGPL", "FIRST", ObjectType.File, "FIRST", 1), JobLockMode.Exclusive, TimeSpan.Zero)) { }
    }
    [Fact]
    public async Task Authority_revoked_while_waiting_for_the_writer_prevents_a_current_record_update()
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", Authorities.ChangeBits);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QUSER", job.Key))
        { cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", cancellationToken: stop.Token); Assert.NotNull(cursor.Read()); }
        using (cursor)
        using (var blocker = _system.Connections.Open())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            var writer = Task.Run(() => { using var identity = OperationIdentity.Enter("QUSER", job.Key); cursor.Update(PointText("revoked")); });
            try
            {
                var locks = new JobLockStore(_system.Connections);
                Assert.True(SpinWait.SpinUntil(() => locks.Inspect(new("QGPL", "FIRST", ObjectType.File)).Any(held => held.Lifetime == "RecordMutation"), TimeSpan.FromSeconds(5)), "Writer must acquire its record lease before authority is revoked.");
                using var command = blocker.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE sys_authorities SET bits=$bits WHERE lib='QGPL' AND name='FIRST' AND holder='QUSER' AND is_authl=0";
                command.Parameters.AddWithValue("$bits", (int)Authorities.UseBits); Assert.Equal(1, command.ExecuteNonQuery()); transaction.Commit();
                Assert.Equal("CPF9802", (await Assert.ThrowsAsync<CpfException>(() => writer.WaitAsync(TimeSpan.FromSeconds(5)))).MessageId);
                Assert.NotNull(cursor.Current); Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            }
            finally
            {
                stop.Cancel(); transaction.Dispose();
                try { await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (CpfException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}
