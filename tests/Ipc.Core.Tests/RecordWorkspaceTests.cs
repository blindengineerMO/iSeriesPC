using Ipc.Core.Objects;
using Ipc.Services.Events;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    private void SeedLargeUnrelatedJournaledMember()
    {
        using var connection = _system.Connections.Open(); connection.CreateFunction("ipc_journal_replay", () => 1);
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=zeroblob(16777216)"; command.ExecuteNonQuery();
    }
    private void AssertSmallWorkspaceWithoutSecond()
    {
        var path = WorkspacePath(); Assert.Equal(0L, WorkspaceValue(path, "SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
        var bytes = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "records.db*").Sum(file => new FileInfo(file).Length);
        Assert.True(bytes < 4 * 1024 * 1024, "An unrelated journaled member must not expand the record workspace: " + bytes);
    }
    [Fact]
    public void Opening_a_record_scope_and_running_a_scalar_query_does_not_materialize_members()
    {
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        { Assert.Equal(1L, Assert.Single(statement.Query("SELECT 1"))[0]); statement.Complete(); }
        Assert.Equal(0L, WorkspaceValue(WorkspacePath(), "SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        AssertSmallWorkspaceWithoutSecond();
    }
    [Fact]
    public void SQL_does_not_copy_a_large_unreferenced_journaled_member_even_when_enlisted()
    {
        SeedLargeUnrelatedJournaledMember(); Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        { Assert.Equal("original", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""))[0]); statement.Complete(); }
        AssertSmallWorkspaceWithoutSecond();
        Assert.Equal(1L, WorkspaceValue(WorkspacePath(), "SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void File_store_writes_do_not_copy_unrelated_enlisted_journaled_files()
    {
        SeedLargeUnrelatedJournaledMember(); Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
            _files.Update("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "file pending" });
        AssertSmallWorkspaceWithoutSecond();
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("file pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void File_store_member_access_does_not_copy_other_members_of_the_same_file()
    {
        _files.AddMember("QGPL", "FIRST", "LARGE");
        using (var connection = _system.Connections.Open())
        {
            connection.CreateFunction("ipc_journal_replay", () => 1);
            using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO \"QGPL.FIRST.LARGE\" VALUES(2,zeroblob(16777216))"; command.ExecuteNonQuery();
        }
        Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
            _files.Update("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "small member" });
        Assert.Equal(0L, WorkspaceValue(WorkspacePath(), "SELECT count(*) FROM \"QGPL.FIRST.LARGE\""));
        AssertSmallWorkspaceWithoutSecond(); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("small member", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void SQL_joins_load_all_referenced_members_before_execution()
    {
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal(new object?[] { "original", "original" }, Assert.Single(statement.Query("SELECT f.TEXT,s.TEXT FROM \"QGPL.FIRST.FIRST\" f JOIN \"QGPL.SECOND.SECOND\" s ON f.ID=s.ID")));
            statement.Complete();
        }
        Assert.Equal(1L, WorkspaceValue(WorkspacePath(), "SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void Lazy_member_imports_preserve_the_callers_last_insert_rowid()
    {
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            Assert.Equal(0L, Assert.Single(statement.Query("SELECT last_insert_rowid()"))[0]);
            statement.Execute("INSERT INTO \"QGPL.FIRST.FIRST\" VALUES(2,'new record')");
            Assert.Equal(2L, Assert.Single(statement.Query("SELECT last_insert_rowid()"))[0]);
            Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
            Assert.Equal(2L, Assert.Single(statement.Query("SELECT last_insert_rowid()"))[0]);
            statement.Execute("INSERT INTO \"QGPL.FIRST.FIRST\" VALUES(last_insert_rowid()+1,'uses prior identity')"); statement.Complete();
        }
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal(3L, Sql("SELECT max(ID) FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void A_multi_statement_batch_loads_its_members_before_any_mutation()
    {
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='first'; UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='second'"); statement.Complete();
        }
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); Assert.Equal("second", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void A_later_member_load_uses_the_scopes_original_committed_snapshot()
    {
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal("original", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""))[0]);
            Sql("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='newly committed'");
            _system.Jobs.WriteLog(_job.Key, "INFO", "IPCS0001", 0, "writer remains independent");
            Assert.Equal("original", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]); statement.Complete();
        }
        using (var next = _system.RecordTransactions.Open(_job.Key))
            Assert.Equal("newly committed", Assert.Single(next.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]);
        using var connection = _system.Connections.Open(); using var checkpoint = connection.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = checkpoint.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(0L, reader.GetInt64(0));
    }
    [Fact]
    public void Unreferenced_pending_members_remain_unloaded_and_still_publish()
    {
        Begin(); Change("SECOND", "second pending");
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        { Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\"")); statement.Complete(); }
        AssertSmallWorkspaceWithoutSecond(); Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("second pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void A_later_load_replays_prior_intents_once_and_keeps_this_scopes_changes()
    {
        Begin(); Change("SECOND", "prior pending");
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            Assert.Equal("prior pending", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]);
            statement.Execute("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='next pending'");
            Assert.Equal("next pending", Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]); statement.Complete();
        }
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("next pending", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void Loading_a_member_does_not_fire_its_insert_trigger_or_duplicate_pending_changes()
    {
        Sql("CREATE TRIGGER insert_effect AFTER INSERT ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE \"QGPL.SECOND.SECOND\" SET ID=ID+1; END");
        Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        { statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='pending'"); statement.Complete(); }
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Equal(1L, Assert.Single(statement.Query("SELECT ID FROM \"QGPL.SECOND.SECOND\""))[0]);
            statement.Execute("INSERT INTO \"QGPL.FIRST.FIRST\" VALUES(2,'inserted')"); statement.Complete();
        }
        Assert.Equal(3, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(2L, Sql("SELECT ID FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void File_store_trigger_dependencies_load_before_the_actual_mutation()
    {
        Sql("CREATE TRIGGER dependent_output AFTER UPDATE ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=NEW.TEXT; END"); Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
            _files.Update("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "trigger output" });
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal("trigger output", Sql("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void Nested_trigger_programs_load_every_dependent_member_before_execution()
    {
        _files.CreatePhysicalFile("QGPL", "THIRD", _files.GetDefinition("QGPL", "FIRST")!, "");
        _system.Journals.StartFile(new("QGPL", "THIRD"), Journal);
        _files.Insert("QGPL", "THIRD", "THIRD", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "original" });
        Sql("CREATE TRIGGER first_hop AFTER UPDATE ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE \"QGPL.SECOND.SECOND\" SET TEXT=NEW.TEXT; END; CREATE TRIGGER second_hop AFTER UPDATE ON \"QGPL.SECOND.SECOND\" BEGIN UPDATE \"QGPL.THIRD.THIRD\" SET TEXT=NEW.TEXT; END");
        Begin(); Change("FIRST", "nested effects"); Assert.Equal(3, _system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("nested effects", Sql("SELECT TEXT FROM \"QGPL.THIRD.THIRD\""));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_or_cancelled_scopes_release_the_source_snapshot(bool cancel)
    {
        Begin(); using var cancellation = new CancellationTokenSource();
        using (var statement = _system.RecordTransactions.Open(_job.Key, cancellation.Token))
        {
            Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            Sql("UPDATE \"QGPL.SECOND.SECOND\" SET TEXT='committed after snapshot'");
            if (cancel)
            {
                cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
            }
            else Assert.Throws<SqliteException>(() => statement.Query("SELECT missing_column FROM \"QGPL.SECOND.SECOND\""));
        }
        using (var connection = _system.Connections.Open())
        {
            using var checkpoint = connection.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var reader = checkpoint.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(0L, reader.GetInt64(0));
        }
        using var next = _system.RecordTransactions.Open(_job.Key);
        Assert.Equal("committed after snapshot", Assert.Single(next.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""))[0]);
    }
    [Fact]
    public void File_store_foreign_key_cascades_load_the_dependent_member()
    {
        _system.Journals.EndFile(Second);
        Sql("DROP TABLE \"QGPL.SECOND.SECOND\"; CREATE UNIQUE INDEX parent_key ON \"QGPL.FIRST.FIRST\"(ID); CREATE TABLE \"QGPL.SECOND.SECOND\"(ID INTEGER REFERENCES \"QGPL.FIRST.FIRST\"(ID) ON DELETE CASCADE,TEXT TEXT); INSERT INTO \"QGPL.SECOND.SECOND\" VALUES(1,'child')");
        _system.Journals.StartFile(Second, Journal, "*BOTH"); Begin();
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
            _files.Delete("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 1L });
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(0L, Sql("SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void File_store_inserts_load_referenced_parent_rows_before_foreign_key_checks()
    {
        _system.Journals.EndFile(Second);
        Sql("DROP TABLE \"QGPL.SECOND.SECOND\"; CREATE UNIQUE INDEX insert_parent_key ON \"QGPL.FIRST.FIRST\"(ID); CREATE TABLE \"QGPL.SECOND.SECOND\"(ID INTEGER REFERENCES \"QGPL.FIRST.FIRST\"(ID),TEXT TEXT); INSERT INTO \"QGPL.SECOND.SECOND\" VALUES(1,'existing child')");
        _system.Journals.StartFile(Second, Journal, "*BOTH"); _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, Second);
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
            _files.Insert("QGPL", "SECOND", "SECOND", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "new child" });
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(2L, Sql("SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
    }
    [Fact]
    public void An_invalid_unrelated_trigger_does_not_prevent_reading_or_updating_other_columns()
    {
        Sql("CREATE TRIGGER invalid_insert AFTER INSERT ON \"QGPL.FIRST.FIRST\" BEGIN UPDATE missing_table SET TEXT=NEW.TEXT; END"); Begin();
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            Assert.Single(statement.Query("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
            statement.Execute("UPDATE \"QGPL.FIRST.FIRST\" SET TEXT='valid update'"); statement.Complete();
        }
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) Assert.Single(_files.ReadAll("QGPL", "FIRST", "FIRST"));
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal("valid update", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\""));
    }
    [Fact]
    public void Denied_SQL_member_access_does_not_load_its_records()
    {
        var job = _system.Jobs.CreateInteractive("QUSER");
        _system.Security.Authority.Grant("QGPL", "FIRST", ObjectType.File, "QUSER", Authorities.ChangeBits);
        _system.Security.Authority.Grant("QGPL", "SECOND", ObjectType.File, "QUSER", AuthorityBit.ObjectOperate);
        string path;
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            _system.RecordTransactions.Begin(job.Key); path = Path.Combine(_directory, ".record-units", _system.RecordTransactions.Inspect(job.Key).Definition, "records.db");
            using var statement = _system.RecordTransactions.Open(job.Key);
            Assert.Throws<SqliteException>(() => statement.Query("SELECT TEXT FROM \"QGPL.SECOND.SECOND\""));
        }
        Assert.Equal(0L, WorkspaceValue(path, "SELECT count(*) FROM \"QGPL.SECOND.SECOND\""));
    }
}
