using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RecordTransactionTests
{
    private void SparseFirstRecords() => Sql("INSERT INTO \"QGPL.FIRST.FIRST\"(rowid,ID,TEXT) VALUES(3,30,'third'),(8,80,'eighth')");
    [Fact]
    public void Relative_reads_and_writes_restore_the_committed_RRN_position_on_rollback()
    {
        SparseFirstRecords(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", keyed: false);
        Assert.True(cursor.ChainRecord(3)); _system.RecordTransactions.Commit(_job.Key);
        Assert.True(cursor.ChainRecord(8)); cursor.Update(new Dictionary<string, object?> { ["TEXT"] = "discarded" });
        _system.RecordTransactions.Rollback(_job.Key); Assert.Null(cursor.CurrentRecordNumber);
        Assert.Equal(80L, cursor.Read()!["ID"]); Assert.Equal(8L, cursor.CurrentRecordNumber); Assert.Equal("eighth", cursor.Current!["TEXT"]);
    }
    [Fact]
    public void A_relative_lower_limit_checkpoint_is_restored_before_its_target()
    {
        SparseFirstRecords(); Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", keyed: false);
        Assert.False(cursor.SetRecordLowerBound(2)); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(30L, cursor.Read()!["ID"]); Assert.Equal(80L, cursor.Read()!["ID"]);
        _system.RecordTransactions.Rollback(_job.Key); Assert.Equal(30L, cursor.Read()!["ID"]); Assert.Equal(3L, cursor.CurrentRecordNumber);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pending_insert_can_be_changed_or_deleted_by_RRN_before_publication(bool delete)
    {
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        _files.Insert("QGPL", "FIRST", "FIRST", "REC", new Dictionary<string, object?> { ["ID"] = 2L, ["TEXT"] = "pending" });
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", keyed: false);
        Assert.True(cursor.ChainRecord(2)); Assert.Equal(2L, cursor.CurrentRecordNumber);
        if (delete) Assert.True(cursor.DeleteRecord(2)); else cursor.Update(new Dictionary<string, object?> { ["TEXT"] = "updated pending" });
        Assert.Equal(2, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        _system.RecordTransactions.Commit(_job.Key); Assert.Equal(delete ? 1L : 2L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        if (!delete) Assert.Equal("updated pending", Sql("SELECT TEXT FROM \"QGPL.FIRST.FIRST\" WHERE rowid=2"));
    }
    [Fact]
    public void RRN_values_above_signed_32_bit_range_survive_workspace_intents_and_journal_publication()
    {
        Sql("INSERT INTO \"QGPL.FIRST.FIRST\"(rowid,ID,TEXT) VALUES(4294967288,999,'maximum')");
        Begin(); using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        using var cursor = _files.OpenSequentialCursor("QGPL", "FIRST", "FIRST", keyed: false);
        Assert.True(cursor.ChainRecord(4294967288)); Assert.True(cursor.DeleteRecord(4294967288)); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.FIRST.FIRST\""));
        Assert.Equal(4294967288L, Sql("SELECT record_number FROM sys_journal_entries WHERE entry_type='DL' ORDER BY sequence DESC LIMIT 1"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiled_unkeyed_RPG_RRN_operations_share_the_job_unit_and_journal(bool physicalKeys)
    {
        var format = new RecordFormat { Name = "RROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = physicalKeys ? 1 : 0 }, new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "RDATA", new() { Name = "RDATA", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _system.Journals.StartFile(new("QGPL", "RDATA"), Journal);
        foreach (var id in new long[] { 101, 202 }) _files.Insert("QGPL", "RDATA", "RDATA", "RROW", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = "original" });
        Begin(); _system.RecordTransactions.Enlist(_job.Key, new("QGPL", "RDATA"));
        using var identity = OperationIdentity.Enter("QSECOFR", _job.Key);
        var access = new RpgSqliteFileAccess(_files);
        void Run(string body) => new RpgInterpreter(new RpgHost { Files = access, OpenFile = _ => {
            var cursor = new RpgFileCursor(access, "QGPL", "RDATA", "RDATA", keyed: false); return new RpgFileHandle(() => cursor, cursor.Dispose);
        } })
            .Run(RpgCompiler.Compile("QGPL", "RRNTX", "**free\ndcl-s ID int(20);\ndcl-s TEXT char(100);\n" + body + "\nreturn;"));
        Run("chain 2 RDATA;\neval TEXT='discarded';\nupdate RDATA;");
        Assert.Equal(1, _system.RecordTransactions.Inspect(_job.Key).PendingChanges); Assert.Equal("original", Sql("SELECT TEXT FROM \"QGPL.RDATA.RDATA\" WHERE rowid=2"));
        _system.RecordTransactions.Rollback(_job.Key); Run("delete 2 RDATA;"); _system.RecordTransactions.Commit(_job.Key);
        Assert.Equal(101L, Sql("SELECT ID FROM \"QGPL.RDATA.RDATA\"")); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RDATA.RDATA\""));
        Assert.Equal(2L, Sql("SELECT record_number FROM sys_journal_entries WHERE entry_type='DL' ORDER BY sequence DESC LIMIT 1"));
    }
}
