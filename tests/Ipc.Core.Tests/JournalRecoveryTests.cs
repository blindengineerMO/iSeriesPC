using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session.Transport;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed partial class JournalRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;
    private static readonly QualifiedName Journal = new("QGPL", "RECOVERY");
    private static readonly QualifiedName FileName = new("QGPL", "RECORDS");
    private static readonly JournalFileSelection[] Selection = [new(FileName, "*ALL")];
    public JournalRecoveryTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RecoveryAdministrator30");
        _system.Journals.CreateReceiver(new("QGPL", "RCV0001")); _system.Journals.Create(Journal, new("QGPL", "RCV0001")); CreateFile("RECORDS");
    }
    private void CreateFile(string name)
    {
        var format = new RecordFormat { Name = "REC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 }, new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100, NullCapable = true }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", name, new() { Name = name, Attribute = FileAttribute.Physical, Formats = [format] }, "");
    }
    private void Start(string images = "*BOTH") => _system.Journals.StartFile(FileName, Journal, images);
    private void Insert(long id, string text = "first", string file = "RECORDS") => _files.Insert("QGPL", file, file, "REC", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = text });
    private void Update(long id, string text) => _files.Update("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = text });
    private object? Sql(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void RestoreBaseline(string sql)
    { using var connection = _system.Connections.Open(); connection.CreateFunction("ipc_journal_replay", () => 1); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    [Fact]
    public void Recovery_reinsertion_of_identical_values_invalidates_an_ordinary_cursor_target()
    {
        Start(); Insert(1); using var cursor = _files.OpenSequentialCursor("QGPL", "RECORDS", "RECORDS", keyed: false);
        Assert.True(cursor.ChainRecord(1));
        _system.Journals.Recover(Journal, Selection, 1, 1, remove: true);
        _system.Journals.Recover(Journal, Selection, 1, 1);
        Assert.Equal("CPF5026", Assert.Throws<CpfException>(() => cursor.Delete()).MessageId);
        Assert.True(cursor.ChainRecord(1)); cursor.Update(new Dictionary<string, object?> { ["TEXT"] = "fresh target" });
        Assert.Equal("fresh target", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public void Mixed_records_remove_in_reverse_and_apply_forward_with_exact_RRNs_and_recovery_markers()
    {
        Start(); Insert(1); Insert(2, "second"); Update(1, "changed");
        _files.Delete("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 2L }); Insert(3, "third");
        Assert.Equal(6, _system.Journals.Inspect(Journal).Sequence);
        var removed = _system.Journals.Recover(Journal, Selection, 6, 1, remove: true);
        Assert.Equal(new JournalRecoveryResult(5, 6, 1), removed); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
        Assert.Equal(new[] { "SR", "RC" }, _system.Journals.Read(Journal, 7).Select(e => e.Type));
        var applied = _system.Journals.Recover(Journal, Selection, 1, 6);
        Assert.Equal(new JournalRecoveryResult(5, 1, 6), applied);
        Assert.Equal("1:1:changed,3:3:third", Sql("SELECT group_concat(rowid||':'||ID||':'||TEXT) FROM \"QGPL.RECORDS.RECORDS\" ORDER BY rowid"));
        Assert.Equal(6L, Sql("SELECT count(*) FROM sys_journal_entries WHERE code='R'"));
        Assert.Equal(10, _system.Journals.Inspect(Journal).Sequence);
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 6)); Assert.Equal(10, _system.Journals.Inspect(Journal).Sequence);
        Insert(4); Assert.Equal("PT", _system.Journals.Read(Journal)[^1].Type);
    }
    [Fact]
    public void Update_pair_boundaries_and_prior_recovery_boundaries_fail_atomically()
    {
        Start(); Insert(1); Update(1, "changed");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 3, 3, true));
        Assert.Equal("changed", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal(3, _system.Journals.Inspect(Journal).Sequence);
        _system.Journals.Recover(Journal, Selection, 3, 2, true); Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 5, 1, true)); Assert.Equal(5, _system.Journals.Inspect(Journal).Sequence);
        _system.Journals.Recover(Journal, Selection, 2, 3); Assert.Equal("changed", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public void After_only_updates_apply_but_cannot_be_removed_without_before_images()
    {
        Start("*AFTER"); Insert(1); Update(1, "after");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 2, 1, true));
        RestoreBaseline("UPDATE \"QGPL.RECORDS.RECORDS\" SET TEXT='baseline'");
        Assert.Equal(1, _system.Journals.Recover(Journal, Selection, 2, 2).Changes);
        Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public void Data_mismatch_and_end_marker_failure_restore_data_triggers_sequence_and_receiver_accounting()
    {
        Start(); Insert(1); Update(1, "after");
        var triggerCount = Sql("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='QGPL.RECORDS.RECORDS'");
        RestoreBaseline("UPDATE \"QGPL.RECORDS.RECORDS\" SET TEXT='unexpected'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 3, 1, true));
        Assert.Equal("unexpected", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal(3, _system.Journals.Inspect(Journal).Sequence);
        RestoreBaseline("UPDATE \"QGPL.RECORDS.RECORDS\" SET TEXT='after'");
        var bytes = _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes;
        Sql("CREATE TRIGGER fail_recovery_end BEFORE INSERT ON sys_journal_entries WHEN NEW.code='F' AND NEW.entry_type='RC' BEGIN SELECT RAISE(ABORT,'injected end failure'); END");
        Assert.Throws<SqliteException>(() => _system.Journals.Recover(Journal, Selection, 3, 1, true));
        Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal(3, _system.Journals.Inspect(Journal).Sequence);
        Assert.Equal(bytes, _system.Journals.InspectReceiver(new("QGPL", "RCV0001")).Bytes);
        Assert.Equal(triggerCount, Sql("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='QGPL.RECORDS.RECORDS'"));
        Assert.Equal(1L, Sql("PRAGMA foreign_keys")); Insert(2); Assert.Equal(4, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void Foreign_keys_and_trigger_side_effects_are_recovered_once_across_multiple_files()
    {
        CreateFile("CHILD");
        Sql("CREATE UNIQUE INDEX parent_key ON \"QGPL.RECORDS.RECORDS\"(ID); DROP TABLE \"QGPL.CHILD.CHILD\"; CREATE TABLE \"QGPL.CHILD.CHILD\"(ID INTEGER,TEXT TEXT REFERENCES \"QGPL.RECORDS.RECORDS\"(ID) ON DELETE CASCADE); CREATE TABLE trigger_effect(n INTEGER); CREATE TRIGGER dependent AFTER INSERT ON \"QGPL.RECORDS.RECORDS\" BEGIN INSERT INTO \"QGPL.CHILD.CHILD\" VALUES(NEW.ID,CAST(NEW.ID AS TEXT)); INSERT INTO trigger_effect VALUES(NEW.ID); END");
        Start(); _system.Journals.StartFile(new("QGPL", "CHILD"), Journal, "*BOTH"); Insert(1);
        var end = _system.Journals.Inspect(Journal).Sequence; Assert.Equal(2, end);
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, end, 1, true)); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\""));
        JournalFileSelection[] all = [new(FileName, "*ALL"), new(new("QGPL", "CHILD"), "*ALL")];
        _system.Journals.Recover(Journal, all, end, 1, true); Assert.Equal(0L, Sql("SELECT count(*) FROM \"QGPL.CHILD.CHILD\""));
        _system.Journals.Recover(Journal, all, 1, end); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.CHILD.CHILD\"")); Assert.Equal(1L, Sql("SELECT count(*) FROM trigger_effect"));
        Assert.Null(Sql("PRAGMA foreign_key_check")); Insert(2); Assert.Equal(2L, Sql("SELECT count(*) FROM trigger_effect"));
    }
    [Fact]
    public void Explicit_receiver_chain_recovers_across_rotation_and_rejects_purged_history()
    {
        Start(); Insert(1); var next = _system.Journals.Rotate(Journal); Insert(2);
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 2, 1, true));
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 2, 1, true, new("QGPL", "RCV0001"), next));
        Assert.Equal(2, _system.Journals.Recover(Journal, Selection, 2, 1, true, next, new("QGPL", "RCV0001")).Changes);
        _system.Journals.Recover(Journal, Selection, 1, 2, false, new("QGPL", "RCV0001"), next);
        _system.Journals.DeleteReceiver(new("QGPL", "RCV0001"), true);
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 2, 1, true)); Assert.Equal(2, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Fact]
    public void File_rename_retains_recovery_identity_and_rebinding_cannot_replay_old_entries()
    {
        Start(); Insert(1); _system.ObjectOperations.Relocate(FileName, ObjectType.File, new("QGPL", "RENAMED"));
        _system.Journals.Recover(Journal, [new(new("QGPL", "RENAMED"), "*ALL")], 1, 1, true);
        _system.Journals.Recover(Journal, [new(new("QGPL", "RENAMED"), "*ALL")], 1, 1);
        _system.Journals.EndFile(new("QGPL", "RENAMED")); _system.Journals.StartFile(new("QGPL", "RENAMED"), Journal, "*BOTH");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, [new(new("QGPL", "RENAMED"), "*ALL")], 1, 1, true));
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RENAMED.RECORDS\""));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changed_column_or_record_format_rejects_recovery_before_any_data_change(bool definition)
    {
        Start(); Insert(1);
        Sql(definition ? "UPDATE sys_file_defs SET def=json_set(def,'$.formats[0].fields[1].length',101) WHERE name='RECORDS'" : "ALTER TABLE \"QGPL.RECORDS.RECORDS\" ADD COLUMN EXTRA TEXT");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, true));
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void Recovery_preserves_integer_binary_null_and_IEEE754_storage_without_loss()
    {
        Start(); Sql("INSERT INTO \"QGPL.RECORDS.RECORDS\" VALUES(9223372036854775807,X'00FF'); INSERT INTO \"QGPL.RECORDS.RECORDS\" VALUES(1.0000000000000002,NULL)");
        _system.Journals.Recover(Journal, Selection, 2, 1, true); _system.Journals.Recover(Journal, Selection, 1, 2);
        Assert.Equal(long.MaxValue, Sql("SELECT ID FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=1")); Assert.Equal("00FF", Sql("SELECT hex(TEXT) FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=1"));
        Assert.Equal("3FF0000000000001", Sql("SELECT ipc_journal_real(ID) FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=2")); Assert.IsType<DBNull>(Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\" WHERE rowid=2"));
    }
    [Fact]
    public void Recovery_streams_a_range_larger_than_the_entry_display_limit()
    {
        Start(); Sql("WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10005) INSERT INTO \"QGPL.RECORDS.RECORDS\" SELECT x,CAST(x AS TEXT) FROM n");
        Assert.Equal(10005, _system.Journals.Recover(Journal, Selection, 10005, 1, true).Changes);
        Assert.Equal(10005, _system.Journals.Recover(Journal, Selection, 1, 10005).Changes);
        Assert.Equal(10005, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Theory]
    [InlineData("after_image='[[]]'")]
    [InlineData("after_image='[[\"ID\",\"invalid\",\"1\"]]'")]
    [InlineData("after_image=NULL")]
    [InlineData("schema_description='{}'")]
    [InlineData("record_number=1.5")]
    public void Corrupt_record_images_schemas_and_numbers_fail_without_losing_data_or_triggers(string corrupt)
    {
        Start(); Insert(1);
        var triggerCount = Sql("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='QGPL.RECORDS.RECORDS'");
        Sql("UPDATE sys_journal_entries SET " + corrupt + " WHERE code='R'");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, true));
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence);
        Assert.Equal(triggerCount, Sql("SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name='QGPL.RECORDS.RECORDS'"));
    }
    [Fact]
    public async Task Cancellation_while_waiting_and_live_authority_denial_leave_no_recovery_effects()
        => await Task.Factory.StartNew(CheckCanceledRecovery,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private void CheckCanceledRecovery()
    {
        Start(); Insert(1);
        using (var connection = _system.Connections.Open()) using (var held = connection.BeginTransaction(deferred: false))
        using (var stop = new CancellationTokenSource())
        using (var started = new ManualResetEventSlim())
        {
            var recovery = Task.Factory.StartNew(() => { started.Set(); return Record.Exception(() => _system.Journals.Recover(Journal, Selection, 1, 1, true, cancellationToken: stop.Token)); },
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(4)),"Recovery worker did not start.");
                Thread.Sleep(100); stop.Cancel();
                Assert.IsAssignableFrom<OperationCanceledException>(recovery.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult());
            }
            finally
            {
                stop.Cancel(); held.Rollback();
                recovery.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            }
        }
        _system.Security.Authority.Grant("QGPL", "RECOVERY", ObjectType.Journal, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "RECORDS", ObjectType.File, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "RCV0001", ObjectType.JournalReceiver, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Selection, 1, 1, true));
        Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Theory]
    [InlineData(JournalRecoveryErrorPolicy.End, 2, 1, 4)]
    [InlineData(JournalRecoveryErrorPolicy.Continue, 3, 0, 1)]
    public void Native_error_policies_retain_a_consistent_prefix_and_report_failed_file_and_sequence(JournalRecoveryErrorPolicy policy, int changes, int otherCount, int last)
    {
        CreateFile("OTHER"); Start(); _system.Journals.StartFile(new("QGPL", "OTHER"), Journal, "*BOTH");
        Insert(0, "other zero", "OTHER"); Insert(1); Insert(2, "second"); Insert(3, "other three", "OTHER"); Insert(4);
        RestoreBaseline("DELETE FROM \"QGPL.RECORDS.RECORDS\" WHERE ID=2");
        var result = _system.Journals.Recover(Journal, [new(FileName, "*ALL"), new(new("QGPL", "OTHER"), "*ALL")], 5, 1, true, errorPolicy: policy);
        Assert.Equal(changes, result.Changes); Assert.Equal(last, result.LastSequence);
        var failure = Assert.Single(result.Failures!); Assert.Equal(FileName, failure.File); Assert.Equal(3, failure.Sequence);
        Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\"")); Assert.Equal((long)otherCount, Sql("SELECT count(*) FROM \"QGPL.OTHER.OTHER\""));
        var end = _system.Journals.Read(Journal).Last(e => e.Type == "RC" && e.ObjectName == "OTHER");
        using var data = global::System.Text.Json.JsonDocument.Parse(end.Data!); Assert.Equal(policy == JournalRecoveryErrorPolicy.End ? 1 : 0, data.RootElement.GetProperty("Flag").GetInt32());
        Insert(5); Assert.Equal("PT", _system.Journals.Read(Journal)[^1].Type);
    }
    [Fact]
    public void Continue_after_a_member_failure_recovers_other_members_in_the_same_file()
    {
        _files.AddMember("QGPL", "RECORDS", "SECOND"); Start(); Insert(1);
        _files.Insert("QGPL", "RECORDS", "SECOND", "REC", new Dictionary<string, object?> { ["ID"] = 2L });
        RestoreBaseline("DELETE FROM \"QGPL.RECORDS.SECOND\"");
        var result = _system.Journals.Recover(Journal, Selection, 2, 1, true, errorPolicy: JournalRecoveryErrorPolicy.Continue);
        Assert.Equal(1, result.Changes); Assert.Equal("SECOND", Assert.Single(result.Failures!).Member);
        Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "SECOND"));
    }
    [Fact]
    public void Commands_and_compiled_CL_apply_and_remove_member_ranges_through_the_shared_service()
    {
        Start(); Insert(1); Update(1, "after"); var commands = new CommandService(_system);
        void Run(string text) { var result = commands.Execute(text); Assert.False(result.IsError, result.Message + " " + string.Join(" ", result.Listing ?? [])); }
        Run("RMVJRNCHG JRN(QGPL/RECOVERY) FILE((QGPL/RECORDS *ALL)) RCVRNG(*CURRENT) FROMENT(3) TOENT(2) OBJERROPT(*END)");
        Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "REPLAY", "PGM\nDCL &JRN *CHAR LEN(13) VALUE('QGPL/RECOVERY')\nAPYJRNCHG JRN(&JRN) FILE((QGPL/RECORDS *ALL)) RCVRNG(*CURRENT) FROMENT(2) TOENT(3)\nENDPGM", null);
        Run("CRTCLPGM PGM(QGPL/REPLAY) SRCFILE(QGPL/SOURCE)"); Run("CALL QGPL/REPLAY");
        Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        var failed = commands.Execute("APYJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(*CURRENT) FROMENT(1) TOENT(1)"); Assert.True(failed.IsError); Assert.Equal("CPF70A7", failed.MessageId);
    }
    [Theory]
    [InlineData("*FIRST", 0, 1)]
    [InlineData("*ALL", 0, 0)]
    [InlineData("AAA", 1, 0)]
    [InlineData("*DEFAULT", 0, 0)]
    [InlineData("*SEPARATE", 0, 0)]
    public void Command_member_selection_uses_creation_order_all_members_and_repeated_file_elements(string member, int initial, int added)
    {
        _files.AddMember("QGPL", "RECORDS", "AAA"); Start(); Insert(1);
        _files.Insert("QGPL", "RECORDS", "AAA", "REC", new Dictionary<string, object?> { ["ID"] = 2L });
        var selection = member == "*DEFAULT" ? "QGPL/RECORDS" : member == "*SEPARATE" ? "(QGPL/RECORDS RECORDS) (QGPL/RECORDS AAA)" : "(QGPL/RECORDS " + member + ")";
        var result = new CommandService(_system).Execute("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(" + selection + ")");
        Assert.False(result.IsError, result.Message); Assert.Equal(initial, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(added, _files.RowCount("QGPL", "RECORDS", "AAA"));
    }
    [Fact]
    public void Large_sequence_commands_preserve_the_full_number_and_named_receiver_removal_descends()
    {
        Start(); Sql("UPDATE sys_journals SET sequence=9999999999"); Insert(1); var next = _system.Journals.Rotate(Journal); Insert(2);
        var commands = new CommandService(_system);
        var removed = commands.Execute("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(QGPL/RCV0002 QGPL/RCV0001) FROMENTLRG(10000000001) TOENTLRG(10000000000)");
        Assert.False(removed.IsError, removed.Message); Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
        var applied = commands.Execute("APYJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(QGPL/RCV0001 *CURRENT) FROMENTLRG(10000000000) TOENTLRG(10000000001)");
        Assert.False(applied.IsError, applied.Message); Assert.Equal(2, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
    }
    [Fact]
    public void Native_update_endpoints_process_UP_on_apply_and_UB_on_remove_independently()
    {
        Start(); Insert(1); Update(1, "after"); var commands = new CommandService(_system);
        var removed = commands.Execute("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) FROMENT(2) TOENT(2)");
        Assert.False(removed.IsError, removed.Message); Assert.Contains("First 2; last 2", removed.Message); Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        var applied = commands.Execute("APYJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(*CURRENT) FROMENT(3) TOENT(3)");
        Assert.False(applied.IsError, applied.Message); Assert.Contains("First 3; last 3", applied.Message); Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        var sequence = _system.Journals.Inspect(Journal).Sequence;
        Assert.Equal("CPF7069", commands.Execute("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) FROMENT(3) TOENT(3)").MessageId);
        Assert.Equal(sequence, _system.Journals.Inspect(Journal).Sequence); Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Theory]
    [InlineData("RCVRNG(*LASTSAVE) FROMENT(*FIRST) TOENT(*LAST)")]
    [InlineData("RCVRNG(*CURRENT) FROMENT(1) FROMENTLRG(1) TOENT(1)")]
    [InlineData("RCVRNG(*CURRENT) FROMENT(1) TOENT(1) CMTBDY(*INVALID)")]
    [InlineData("RCVRNG(*CURRENT) FROMENT(10000000000) TOENT(10000000000)")]
    [InlineData("RCVRNG(*CURRENT) FROMENT(1) TOENT(1) OUTPUT(*OUTFILE)")]
    [InlineData("RCVRNG(*CURRENT) FROMENT(1)")]
    public void Unsupported_or_ambiguous_command_options_fail_before_recovery(string options)
    {
        Start(); Insert(1);
        Assert.True(new CommandService(_system).Execute("APYJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) " + options).IsError);
        Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence); Assert.Equal(1L, Sql("SELECT count(*) FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public async Task Killing_a_recovery_after_uncommitted_WAL_pages_exist_restores_records_triggers_and_journal_on_restart()
    {
        Start();
        Sql("WITH RECURSIVE n(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10000) INSERT INTO \"QGPL.RECORDS.RECORDS\" SELECT x,printf('%02000d',x) FROM n");
        Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        var start = new global::System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, "journal-recover", _directory, "10000" }) start.ArgumentList.Add(argument);
        using var process = global::System.Diagnostics.Process.Start(start)!;
        try
        {
            await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "ready-" + process.Id)) || process.HasExited);
            File.WriteAllText(Path.Combine(_directory, "start"), "start"); var wal = _system.Connections.DatabasePath + "-wal";
            await JobLockTests.WaitFor(() => process.HasExited || File.Exists(wal) && new FileInfo(wal).Length > 32);
            if (process.HasExited) Assert.Fail("Recovery finished before uncommitted WAL spill: " + await process.StandardError.ReadToEndAsync());
            process.Kill(true); await process.WaitForExitAsync();
            using var reopened = IpcSystem.Create(_directory); reopened.Start();
            var records = new SqliteFileStore(reopened.Connections, reopened.Objects);
            Assert.Equal(10000, records.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(10000, reopened.Journals.Inspect(Journal).Sequence);
            records.Insert("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 10001L });
            Assert.Equal(10001, reopened.Journals.Inspect(Journal).Sequence);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact]
    public async Task Independent_authenticated_socket_jobs_recover_shared_data_and_record_the_executing_job()
    {
        Start(); Insert(1); Update(1, "after");
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "RecoverySocketUser30");
        _system.Security.Authority.Grant("QGPL", "RECORDS", ObjectType.File, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "RECOVERY", ObjectType.Journal, "QUSER", Authorities.AllBits);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            await using var first = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "RecoverySocketUser30", stop.Token);
            await using var second = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "RecoverySocketUser30", stop.Token);
            var result = await first.ExecuteAsync("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(*CURRENT) FROMENT(3) TOENT(2)", stop.Token);
            Assert.True(result.Success, result.Result?.Message); Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
            Assert.Equal(first.Job.ToString(), _system.Journals.Read(Journal)[^1].Job);
            result = await second.ExecuteAsync("APYJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(*CURRENT) FROMENT(2) TOENT(3)", stop.Token);
            Assert.True(result.Success, result.Result?.Message); Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
            Assert.Equal(second.Job.ToString(), _system.Journals.Read(Journal)[^1].Job);
            _system.Security.Authority.Grant("QSYS", "RMVJRNCHG", ObjectType.Command, "QUSER", AuthorityBit.None);
            result = await first.ExecuteAsync("RMVJRNCHG JRN(QGPL/RECOVERY) FILE(QGPL/RECORDS) RCVRNG(*CURRENT) FROMENT(3) TOENT(2)", stop.Token);
            Assert.False(result.Success); Assert.Equal("CPF9802", result.Result?.MessageId); Assert.Equal("after", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    public void Dispose() { _system.Dispose(); try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
