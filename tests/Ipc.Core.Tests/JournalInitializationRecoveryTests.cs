using System.Text.Json;
using System.Text.Json.Nodes;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Console.Session;
using Microsoft.Data.Sqlite;
using static Ipc.Core.Tests.NativePhysicalDdsTests;

namespace Ipc.Core.Tests;

public sealed partial class JournalRecoveryTests
{
    private long Extent(string file = "RECORDS") => (long)Sql("SELECT high_water FROM sys_record_slots WHERE lib='QGPL' AND name='" + file + "' AND mbr='" + file + "'")!;
    private void ChangeInitialization(Action<JsonObject> change, long sequence = 1)
    {
        using var connection = _system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_data FROM sys_journal_entries WHERE entry_type='IZ' AND sequence=$sequence";
        command.Parameters.AddWithValue("$sequence", sequence);
        var payload = JsonNode.Parse((byte[])command.ExecuteScalar()!)!.AsObject(); change(payload);
        command.CommandText = "UPDATE sys_journal_entries SET user_data=$data WHERE entry_type='IZ' AND sequence=$sequence";
        command.Parameters.AddWithValue("$data", JsonSerializer.SerializeToUtf8Bytes(payload)); command.ExecuteNonQuery();
    }
    [Fact]
    public void A_late_unique_failure_rolls_back_the_entire_initialization_entry()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        Sql("CREATE UNIQUE INDEX unique_init ON \"QGPL.RECORDS.RECORDS\"(ID)");
        var sequence = _system.Journals.Inspect(Journal).Sequence;
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1));
        Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(sequence, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void A_foreign_key_failure_at_the_recovery_boundary_restores_initialization_and_receiver_state()
    {
        Sql("DROP TABLE \"QGPL.RECORDS.RECORDS\"; CREATE TABLE init_parent(id INTEGER PRIMARY KEY); INSERT INTO init_parent VALUES(0); CREATE TABLE \"QGPL.RECORDS.RECORDS\"(ID INTEGER REFERENCES init_parent(id),TEXT TEXT)");
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS"); Sql("DELETE FROM init_parent");
        var sequence = _system.Journals.Inspect(Journal).Sequence;
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1)); Assert.Equal(0, Extent());
        Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(sequence, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void Oversized_capture_schema_rolls_back_prepared_initialization_records()
    {
        Start(); using (var connection = _system.Connections.Open()) using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sys_file_defs SET def=json_set(def,'$.text',$text) WHERE lib='QGPL' AND name='RECORDS'";
            command.Parameters.AddWithValue("$text", new string('x', 1048576)); command.ExecuteNonQuery();
        }
        Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3));
        Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Empty(_system.Journals.Read(Journal));
    }
    [Fact]
    public void Initialization_recovery_round_trip_survives_reopening_the_system()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _system.Dispose();
        using var reopened = Ipc.Services.IpcSystem.Create(_directory); reopened.Start();
        var files = new SqliteFileStore(reopened.Connections, reopened.Objects);
        reopened.Journals.Recover(Journal, Selection, 1, 1, remove: true); Assert.Equal(0, files.RowCount("QGPL", "RECORDS", "RECORDS"));
        reopened.Journals.Recover(Journal, Selection, 1, 1); Assert.Equal(3, files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Fact]
    public void A_changed_generated_default_rolls_back_every_insert_in_the_initialization_entry()
    {
        Sql("ALTER TABLE \"QGPL.RECORDS.RECORDS\" ADD COLUMN EXTRA TEXT GENERATED ALWAYS AS(coalesce(upper(TEXT),'NONE')) VIRTUAL");
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        ChangeInitialization(root => { var cells = JsonNode.Parse(root["DefaultImage"]!.GetValue<string>())!.AsArray(); cells[2]![2] = "WRONG"; root["DefaultImage"] = cells.ToJsonString(); });
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1)); Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_recovery_honors_the_explicit_member_selection(bool last)
    {
        _files.AddMember("QGPL", "RECORDS", "OTHER"); Start();
        _files.InitializeMember("QGPL", "RECORDS", "RECORDS", totalRecords: 2); _files.InitializeMember("QGPL", "RECORDS", "OTHER", totalRecords: 3);
        var selected = new[] { new JournalFileSelection(FileName, last ? "OTHER" : "RECORDS") };
        Assert.Equal(1, _system.Journals.Recover(Journal, selected, 2, 1, remove: true).Changes);
        Assert.Equal(last ? 2 : 0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(last ? 0 : 3, _files.RowCount("QGPL", "RECORDS", "OTHER"));
        Assert.Equal(1, _system.Journals.Recover(Journal, selected, 1, 2).Changes);
        Assert.Equal(2, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(3, _files.RowCount("QGPL", "RECORDS", "OTHER"));
    }
    [Fact]
    public void A_format_change_since_capture_rejects_initialization_recovery_without_removing_records()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        Sql("UPDATE sys_file_defs SET def=json_set(def,'$.formats[0].fields[1].length',99) WHERE lib='QGPL' AND name='RECORDS'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, remove: true)); Assert.Equal(3, Extent());
        Assert.Equal(3L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public void Historical_initialization_without_a_schema_snapshot_is_not_replayed_using_current_defaults()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); ChangeInitialization(root => root["Format"] = "IPCINIT1");
        Sql("UPDATE sys_journal_entries SET schema_description=NULL WHERE entry_type='IZ'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, remove: true)); Assert.Equal(3, Extent());
    }
    [Fact]
    public void Application_triggers_are_suspended_and_restored_during_initialization_recovery()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        Sql("CREATE TABLE effects(n INTEGER); CREATE TRIGGER recover_insert AFTER INSERT ON \"QGPL.RECORDS.RECORDS\" BEGIN INSERT INTO effects VALUES(1); END; CREATE TRIGGER recover_delete AFTER DELETE ON \"QGPL.RECORDS.RECORDS\" BEGIN INSERT INTO effects VALUES(2); END");
        _system.Journals.Recover(Journal, Selection, 1, 1, remove: true); _system.Journals.Recover(Journal, Selection, 1, 1);
        Assert.Equal(0L, Sql("SELECT count(*) FROM effects")); Insert(9); Assert.Equal(1L, Sql("SELECT count(*) FROM effects"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_remove_and_apply_preserve_the_original_prefix_gaps_and_later_extent(bool deleted)
    {
        Start(); Insert(1); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 4, deleted: deleted); Insert(9, "later");
        Assert.Equal(5, Extent());
        var removed = _system.Journals.Recover(Journal, Selection, 2, 2, remove: true);
        Assert.Equal(1, removed.Changes); Assert.Equal(2, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(5, Extent());
        var applied = _system.Journals.Recover(Journal, Selection, 2, 2);
        Assert.Equal(1, applied.Changes); Assert.Equal(deleted ? 2 : 5, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(5, Extent());
        Assert.Equal("later", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=5"));
        Assert.Equal(1L, Sql("SELECT ID FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=1"));
        Assert.Equal(2L, Sql("SELECT count(*) FROM sys_journal_entries WHERE code='R'"));
    }
    [Fact]
    public void Recovery_can_reapply_default_initialization_after_member_clear_and_then_append_after_its_extent()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS"); Assert.Equal(0, Extent());
        _system.Journals.Recover(Journal, Selection, 1, 1); Assert.Equal(3, Extent());
        Assert.Equal(3, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Insert(4); Assert.Equal(4, Extent());
    }
    [Fact]
    public void Maximum_deleted_initialization_recovers_without_enumerating_or_shrinking_reserved_positions()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "RECORDS", null, memberSize: new(Unlimited: true)); Start();
        _files.InitializeMember("QGPL", "RECORDS", totalRecords: 4294967288, deleted: true); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        _system.Journals.Recover(Journal, Selection, 1, 1); Assert.Equal(4294967288, Extent());
        _system.Journals.Recover(Journal, Selection, 1, 1, remove: true); Assert.Equal(4294967288, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Theory]
    [InlineData(37, "café")]
    [InlineData(1208, "東京")]
    public void Initialization_recovery_uses_the_captured_DDS_default_in_its_original_CCSID(int ccsid, string value)
    {
        var source = string.Join('\n', Line('R', "REC"), Line(' ', "ID", "9", 'B', "0", "DFT(7)"), Line(' ', "TEXT", "20", 'A', keywords: "DFT('" + value + "')"));
        _files.CreatePhysicalFile("QGPL", "DEFAULTS", new DdsCompiler().CompilePhysical("DEFAULTS", source, ccsid), source);
        _system.Journals.StartFile(new("QGPL", "DEFAULTS"), Journal, "*BOTH");
        _files.InitializeMember("QGPL", "DEFAULTS", totalRecords: 3);
        JournalFileSelection[] files = [new(new("QGPL", "DEFAULTS"))];
        _system.Journals.Recover(Journal, files, 1, 1, remove: true); _system.Journals.Recover(Journal, files, 1, 1);
        Assert.All(_files.ReadAll("QGPL", "DEFAULTS", "DEFAULTS"), row => { Assert.Equal(7L, row["ID"]); Assert.Equal(value, row["TEXT"]); });
    }
    [Fact]
    public void Initialization_remove_reinsert_does_not_revive_a_stale_current_record_target()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        using var cursor = _files.OpenSequentialCursor("QGPL", "RECORDS", "RECORDS", keyed: false); Assert.True(cursor.ChainRecord(2));
        _system.Journals.Recover(Journal, Selection, 1, 1, remove: true); _system.Journals.Recover(Journal, Selection, 1, 1);
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Delete()).MessageId); Assert.True(cursor.ChainRecord(2)); cursor.Delete();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_rejects_an_occupied_deleted_initialization_position_without_data_loss(bool remove)
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3, deleted: true);
        RestoreBaseline("INSERT INTO \"QGPL.RECORDS.RECORDS\"(rowid,ID,TEXT) VALUES(2,77,'unrelated')");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, remove: remove));
        Assert.Equal("unrelated", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=2")); Assert.Equal(3, Extent()); Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence);
    }
    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    public void Default_initialization_removal_validates_every_record_before_deleting_any(string defect)
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        RestoreBaseline(defect == "missing" ? "DELETE FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=3" : "UPDATE \"QGPL.RECORDS.RECORDS\" SET TEXT='changed' WHERE rowid=3");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, remove: true));
        Assert.Equal(defect == "missing" ? 2 : 3, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=1"));
    }
    [Theory]
    [InlineData("Format")]
    [InlineData("BeforeExtent")]
    [InlineData("AfterExtent")]
    [InlineData("Count")]
    [InlineData("Flag")]
    [InlineData("DefaultImage")]
    [InlineData("Extra")]
    public void Corrupt_initialization_payloads_fail_before_member_or_receiver_mutation(string field)
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        ChangeInitialization(root => { root[field] = field switch { "Format" => JsonValue.Create("IPCINIT1"), "BeforeExtent" => JsonValue.Create(-1),
            "AfterExtent" => JsonValue.Create(4294967289L), "Count" => JsonNode.Parse("3.0"), "Flag" => JsonValue.Create(2),
            "DefaultImage" => JsonValue.Create("[]"), _ => JsonValue.Create(1) }; });
        var entries = _system.Journals.Inspect(Journal).Sequence; var bytes = _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes;
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1));
        Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(entries, _system.Journals.Inspect(Journal).Sequence); Assert.Equal(bytes, _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes);
    }
    [Theory]
    [InlineData("schema_description=NULL")]
    [InlineData("schema_description='{}'")]
    [InlineData("record_number=2")]
    [InlineData("user_data=NULL")]
    [InlineData("user_data='not a blob'")]
    [InlineData("before_image='[]'")]
    public void Missing_schema_and_invalid_header_payloads_do_not_guess_the_current_layout(string assignment)
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        Sql("UPDATE sys_journal_entries SET " + assignment + " WHERE entry_type='IZ'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1)); Assert.Equal(0, Extent());
    }
    [Fact]
    public void Recovery_rejects_oversized_materialization_before_allocating_rows()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); _files.ClearMember("QGPL", "RECORDS", "RECORDS");
        ChangeInitialization(root => { root["AfterExtent"] = 1000001; root["Count"] = 1000001; }); Sql("UPDATE sys_journal_entries SET record_number=1000001 WHERE entry_type='IZ'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1)); Assert.Equal(0, Extent());
    }
    [Fact]
    public void Failed_initialization_recovery_end_marker_restores_rows_versions_triggers_and_accounting()
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        using var cursor = _files.OpenSequentialCursor("QGPL", "RECORDS", "RECORDS", keyed: false); Assert.True(cursor.ChainRecord(1));
        var bytes = _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes;
        Sql("CREATE TRIGGER fail_init_recovery BEFORE INSERT ON sys_journal_entries WHEN NEW.entry_type='RC' BEGIN SELECT RAISE(ABORT,'failure'); END");
        Assert.Throws<SqliteException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, remove: true));
        Assert.Equal(3, Extent()); Assert.Equal(3, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(bytes, _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes);
        cursor.Update(new Dictionary<string, object?> { ["TEXT"] = "still valid" });
    }
    [Fact]
    public void Generated_values_are_recomputed_and_verified_for_each_initialized_record()
    {
        Sql("ALTER TABLE \"QGPL.RECORDS.RECORDS\" ADD COLUMN EXTRA TEXT GENERATED ALWAYS AS(coalesce(upper(TEXT),'NONE')) VIRTUAL");
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3);
        _system.Journals.Recover(Journal, Selection, 1, 1, remove: true); _system.Journals.Recover(Journal, Selection, 1, 1);
        Assert.Equal(3L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\" WHERE EXTRA='NONE'"));
    }
    [Theory]
    [InlineData(JournalRecoveryErrorPolicy.End, 0)]
    [InlineData(JournalRecoveryErrorPolicy.Continue, 1)]
    public void Initialization_entry_failures_follow_the_native_file_error_policy(JournalRecoveryErrorPolicy policy, int expected)
    {
        CreateFile("OTHER"); Start(); _system.Journals.StartFile(new("QGPL", "OTHER"), Journal, "*BOTH");
        _files.InitializeMember("QGPL", "RECORDS", totalRecords: 2); _files.InitializeMember("QGPL", "OTHER", totalRecords: 2);
        _files.ClearMember("QGPL", "RECORDS", "RECORDS"); _files.ClearMember("QGPL", "OTHER", "OTHER"); ChangeInitialization(root => root["Count"] = -1);
        var result = _system.Journals.Recover(Journal, [new(FileName), new(new("QGPL", "OTHER"))], 1, 2, errorPolicy: policy);
        Assert.Equal(expected, result.Changes); Assert.Single(result.Failures!); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(expected * 2, _files.RowCount("QGPL", "OTHER", "OTHER"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_recovery_rechecks_job_cancellation_and_end_after_writer_wait(bool ended)
    {
        Start(); _files.InitializeMember("QGPL", "RECORDS", totalRecords: 3); var job = _system.Jobs.CreateInteractive("QSECOFR"); Task<JournalRecoveryResult> pending;
        using (var connection = _system.Connections.Open()) using (var held = connection.BeginTransaction(deferred: false))
        {
            pending = Task.Run(() => { using var identity = OperationIdentity.Enter("QSECOFR", job.Key); return _system.Journals.Recover(Journal, Selection, 1, 1, remove: true); });
            await Task.Delay(100); using var command = connection.CreateCommand(); command.Transaction = held;
            command.CommandText = ended ? "UPDATE sys_jobs SET execution_state='Succeeded' WHERE number=$job" : "UPDATE sys_jobs SET cancel_requested=1 WHERE number=$job"; command.Parameters.AddWithValue("$job", job.Key.Number); command.ExecuteNonQuery(); held.Commit();
        }
        if (ended) Assert.Equal("CPF1241", (await Assert.ThrowsAsync<CpfException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).MessageId);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence);
    }
}
