using System.Text.Json;
using Ipc.Cl.Parsing;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;
using Microsoft.Data.Sqlite;
using static Ipc.Core.Tests.NativePhysicalDdsTests;

namespace Ipc.Core.Tests;

public sealed class InitializeMemberTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public InitializeMemberTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        var source = string.Join('\n', Line('R', "REC"), Line(' ', "ID", "9", 'B', "0", "DFT(7)"),
            Line(' ', "LABEL", "8", 'A', keywords: "DFT('seed')"), Line(' ', "AMOUNT", "7", 'P', "2", "DFT(12.50)"));
        _files.CreatePhysicalFile("QGPL", "DATA", new DdsCompiler().CompilePhysical("DATA", source), source);
    }
    public void Dispose() => _system.Dispose();
    private object? Sql(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private long Extent(string member = "DATA") => (long)Sql("SELECT high_water FROM sys_record_slots WHERE lib='QGPL' AND name='DATA' AND mbr='" + member + "'")!;
    private void Command(string text)
    { var result = new CommandService(_system).Execute(text); Assert.False(result.IsError, result.Message); }
    private void Add(long id) => _files.Insert("QGPL", "DATA", "DATA", "REC", new Dictionary<string, object?> { ["ID"] = id });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Custom_members_without_RRNs_are_rejected_before_reserving_space(bool deleted)
    {
        Sql("DROP TABLE \"QGPL.DATA.DATA\"; CREATE TABLE \"QGPL.DATA.DATA\"(ID INTEGER PRIMARY KEY,LABEL TEXT,AMOUNT TEXT) WITHOUT ROWID");
        Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 3, deleted: deleted)); Assert.Equal(0, Extent());
    }
    [Fact]
    public void Source_file_creation_SIZE_controls_its_native_initialization_defaults()
    {
        Command("CRTSRCPF FILE(QGPL/NEWSRC) SIZE(2 2 3)"); Command("INZPFM FILE(QGPL/NEWSRC)");
        Assert.Equal(2, _files.RowCount("QGPL", "NEWSRC", "NEWSRC"));
        Command("INZPFM FILE(QGPL/NEWSRC) RECORDS(*DLT)"); Assert.Equal(2, _files.RowCount("QGPL", "NEWSRC", "NEWSRC"));
        Assert.Equal(4L, Sql("SELECT high_water FROM sys_record_slots WHERE name='NEWSRC'"));
        Assert.True(new CommandService(_system).Execute("CRTSRCPF FILE(QGPL/BADSRC) SIZE(0)").IsError); Assert.False(_files.FileExists("QGPL", "BADSRC"));
    }
    [Fact]
    public void Explicit_SIZE_same_preserves_legacy_definition_bytes()
    {
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Command("CHGPF FILE(QGPL/DATA) SIZE(*SAME *SAME *SAME)"); Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
    }
    [Fact]
    public void SIZE_limits_apply_to_physical_and_logical_additions_and_allow_reusing_a_deleted_position()
    {
        Command("CHGPF FILE(QGPL/DATA) SIZE(2 0 0)"); Add(1); Add(2);
        Assert.Equal("CPF5030", Assert.Throws<CpfException>(() => Add(3)).MessageId);
        var source = LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/DATA)");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source, ["DATA"]);
        void WriteView() => _files.Insert("QGPL", "VIEW", "VIEW", "VREC", new Dictionary<string, object?> { ["ID"] = 3L });
        Assert.Equal("CPF5030", Assert.Throws<CpfException>(WriteView).MessageId);
        Sql("DELETE FROM \"QGPL.DATA.DATA\" WHERE rowid=1"); Command("CHGPF FILE(QGPL/DATA) REUSEDLT(*YES)"); WriteView();
        Assert.Equal(2, Extent()); using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.True(cursor.ChainRecord(1)); Assert.Equal(3L, cursor.Current!["ID"]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_late_copy_SIZE_failure_rolls_back_appended_or_replaced_records_and_extent(bool replace)
    {
        Add(1); Add(2); Add(3);
        var format = _files.GetDefinition("QGPL", "DATA")!.PrimaryFormat;
        _files.CreatePhysicalFile("QGPL", "TARGET", new() { Name = "TARGET", Attribute = FileAttribute.Physical, Formats = [format], MemberSize = new(2, 0, 0) }, "");
        _files.Insert("QGPL", "TARGET", "TARGET", "REC", new Dictionary<string, object?> { ["ID"] = 99L });
        Assert.Equal("CPF5030", Assert.Throws<CpfException>(() => _files.CopyRecords(new("QGPL", "DATA"), "DATA", new("QGPL", "TARGET"), "TARGET", replace: replace)).MessageId);
        Assert.Equal(99L, Assert.Single(_files.ReadAll("QGPL", "TARGET", "TARGET"))["ID"]);
        Assert.Equal(1L, Sql("SELECT high_water FROM sys_record_slots WHERE name='TARGET'"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Job_cancellation_or_end_while_waiting_prevents_initialization(bool ended)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "InitializationAcceptance55!"); var job = _system.Jobs.CreateInteractive("QSECOFR"); Task<long> pending;
        using (var connection = _system.Connections.Open()) using (var held = connection.BeginTransaction(deferred: false))
        {
            pending = Task.Run(() => { using var identity = OperationIdentity.Enter("QSECOFR", job.Key); return _files.InitializeMember("QGPL", "DATA", totalRecords: 3); });
            await Task.Delay(100); using var command = connection.CreateCommand(); command.Transaction = held;
            command.CommandText = ended ? "UPDATE sys_jobs SET execution_state='Succeeded' WHERE number=$job" : "UPDATE sys_jobs SET cancel_requested=1 WHERE number=$job";
            command.Parameters.AddWithValue("$job", job.Key.Number); command.ExecuteNonQuery(); held.Commit();
        }
        if (ended) Assert.Equal("CPF1241", (await Assert.ThrowsAsync<CpfException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).MessageId);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Extent());
    }
    [Fact]
    public void Default_next_increment_in_an_authenticated_job_uses_one_file_allocation()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "InitializationAcceptance55!");
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Equal(10000, _files.InitializeMember("QGPL", "DATA"));
        Assert.Equal(10000, Extent()); Assert.Equal(10000, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Empty(new JobLockStore(_system.Connections).Inspect(new("QGPL", "DATA", ObjectType.File)));
    }
    [Theory]
    [InlineData(37, "café")]
    [InlineData(1208, "東京")]
    public void Initialization_defaults_preserve_the_external_CCSID(int ccsid, string value)
    {
        var source = string.Join('\n', Line('R', "REC"), Line(' ', "TEXT", "12", 'A', keywords: "DFT('" + value + "')"));
        _files.CreatePhysicalFile("QGPL", "TEXTS", new DdsCompiler().CompilePhysical("TEXTS", source, ccsid), source);
        Assert.Equal(2, _files.InitializeMember("QGPL", "TEXTS", totalRecords: 2));
        Assert.All(_files.ReadAll("QGPL", "TEXTS", "TEXTS"), row => Assert.Equal(value, row["TEXT"]));
    }
    [Fact]
    public void Cancellation_and_commitment_enlistment_leave_the_member_unchanged()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 3, cancellationToken: stop.Token));
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "InitializationAcceptance55!");
        StartJournal(); var job = _system.Jobs.CreateInteractive("QSECOFR"); _system.RecordTransactions.Begin(job.Key); _system.RecordTransactions.Enlist(job.Key, new("QGPL", "DATA"));
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 3));
        Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_interrupts_a_writer_wait_without_publishing_partial_records(bool clear)
    {
        Add(99);
        _system.Security.Profiles.Create(new() { Name = "INITUSER" });
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement | AuthorityBit.Add | AuthorityBit.Delete);
        using (var connection = _system.Connections.Open()) using (var held = connection.BeginTransaction(deferred: false)) using (var stop = new CancellationTokenSource())
        {
            // The blocked SQLite call needs a dedicated worker under the full
            // suite's load, rather than competing for a thread-pool worker.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Task.Factory.StartNew(() => {
                using var identity = OperationIdentity.Enter("INITUSER"); entered.SetResult();
                if (clear) { _files.ClearMember("QGPL", "DATA", "DATA", stop.Token); return 0L; }
                return _files.InitializeMember("QGPL", "DATA", totalRecords: 3, cancellationToken: stop.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100); stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                stop.Cancel(); held.Rollback();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
        }
        Assert.Equal(1, Extent());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_authority_changes_during_a_writer_wait_prevent_initialization(bool clear)
    {
        Add(99);
        _system.Security.Profiles.Create(new() { Name = "INITUSER" });
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITUSER", AuthorityBit.ObjectOperate | AuthorityBit.ObjectManagement | AuthorityBit.Add | AuthorityBit.Delete);
        Task<long> pending;
        using (var connection = _system.Connections.Open()) using (var held = connection.BeginTransaction(deferred: false))
        {
            pending = Task.Run(() => { using var identity = OperationIdentity.Enter("INITUSER"); if (clear) { _files.ClearMember("QGPL", "DATA", "DATA"); return 0L; } return _files.InitializeMember("QGPL", "DATA", totalRecords: 3); });
            await Task.Delay(100); using var command = connection.CreateCommand(); command.Transaction = held;
            command.CommandText = "UPDATE sys_authorities SET bits=0 WHERE lib='QGPL' AND name='DATA' AND holder='INITUSER'"; Assert.Equal(1, command.ExecuteNonQuery()); held.Commit();
        }
        Assert.Equal("CPF9802", (await Assert.ThrowsAsync<CpfException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).MessageId); Assert.Equal(1, Extent());
    }
    [Fact]
    public void Source_members_initialize_and_logical_files_are_rejected()
    {
        _files.CreateSourceFile("QGPL", "SOURCE"); Assert.Equal(2, _files.InitializeMember("QGPL", "SOURCE", totalRecords: 2));
        Assert.Equal(2, _files.RowCount("QGPL", "SOURCE", "SOURCE"));
        var source = LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/DATA)");
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("VIEW", source);
        _files.CreateLogicalFile("QGPL", "VIEW", definition, source, ["DATA"]);
        Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "VIEW", totalRecords: 2)); Assert.Equal(0, Extent());
    }
    [Fact]
    public void Default_initialization_appends_DDS_values_without_replacing_existing_or_deleted_slots()
    {
        Add(99); Add(100); Sql("DELETE FROM \"QGPL.DATA.DATA\" WHERE rowid=2");
        Assert.Equal(2, _files.InitializeMember("QGPL", "DATA", totalRecords: 4)); Assert.Equal(4, Extent());
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false);
        Assert.True(cursor.ChainRecord(1)); Assert.Equal(99L, cursor.Current!["ID"]); Assert.False(cursor.ChainRecord(2));
        Assert.True(cursor.ChainRecord(3)); Assert.Equal(7L, cursor.Current!["ID"]); Assert.Equal("seed", cursor.Current["LABEL"]); Assert.Equal(12.50m, cursor.Current["AMOUNT"]);
        Assert.Equal(0, _files.InitializeMember("QGPL", "DATA", totalRecords: 3)); Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
    }
    [Fact]
    public void Deleted_initialization_reserves_the_native_maximum_without_materializing_rows()
    {
        Command("CHGPF FILE(QGPL/DATA) SIZE(*NOMAX)");
        Assert.Equal(4294967288, _files.InitializeMember("QGPL", "DATA", totalRecords: 4294967288, deleted: true));
        Assert.Equal(4294967288, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
        using var cursor = _files.OpenSequentialCursor("QGPL", "DATA", "DATA", keyed: false); Assert.False(cursor.ChainRecord(4294967288));
        Assert.Equal("CPF5030", Assert.Throws<CpfException>(() => Add(1)).MessageId);
        Command("CHGPF FILE(QGPL/DATA) REUSEDLT(*YES)"); Add(1); Assert.True(cursor.ChainRecord(1));
        Assert.Equal(4294967288, Extent());
    }
    [Fact]
    public void Native_command_defaults_and_positional_operands_use_next_increment_and_member_creation_order()
    {
        Command("CHGPF FILE(QGPL/DATA) SIZE(2 2 3)"); _files.AddMember("QGPL", "DATA", "AAA");
        Sql("UPDATE sys_file_members SET created=CASE mbr WHEN 'AAA' THEN '2' ELSE '1' END WHERE name='DATA'");
        Command("INZPFM QGPL/DATA *LAST *DLT"); Assert.Equal(2, Extent("AAA")); Assert.Equal(0, Extent());
        Command("INZPFM FILE(QGPL/DATA)"); Assert.Equal(2, Extent()); Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA"));
        Command("INZPFM QGPL/DATA *FIRST *DLT"); Assert.Equal(4, Extent()); Assert.Equal(2, _files.RowCount("QGPL", "DATA", "DATA"));
        Command("INZPFM FILE(QGPL/DATA) MBR(AAA) RECORDS(*DFT) TOTRCDS(3)"); Assert.Equal(3, Extent("AAA")); Assert.Equal(1, _files.RowCount("QGPL", "DATA", "AAA"));
    }
    [Fact]
    public void SIZE_changes_validate_all_members_and_preserve_metadata_on_failed_combined_changes()
    {
        Command("CHGPF FILE(QGPL/DATA) SIZE(2 2 3)"); _files.InitializeMember("QGPL", "DATA", totalRecords: 6, deleted: true);
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.True(new CommandService(_system).Execute("CHGPF FILE(QGPL/DATA) SIZE(1 0 0) REUSEDLT(*YES) MAXMBRS(2)").IsError);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
        Command("CHGPF FILE(QGPL/DATA) SIZE(*SAME 3 3)"); Assert.Equal(new FileMemberSize(2, 3, 3), _files.GetDefinition("QGPL", "DATA")!.MemberSize);
        Command("CHGPF FILE(QGPL/DATA) SIZE(*NOMAX)");
        Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", deleted: true)); Assert.Equal(6, Extent());
    }
    [Theory]
    [InlineData("SIZE(0)")]
    [InlineData("SIZE(2 0 1)")]
    [InlineData("SIZE(2 1 0)")]
    [InlineData("SIZE(2 32768 1)")]
    [InlineData("SIZE(2 1 1 1)")]
    public void Invalid_creation_SIZE_does_not_publish_a_file(string option)
    {
        _files.CreateSourceFile("QGPL", "DDS"); _files.SaveSourceMember("QGPL", "DDS", "NEW", Line('R', "REC") + "\n" + Line(' ', "TEXT", "5", 'A'), null);
        Assert.True(new CommandService(_system).Execute("CRTPF FILE(QGPL/NEW) SRCFILE(QGPL/DDS) " + option).IsError); Assert.False(_files.FileExists("QGPL", "NEW"));
    }
    [Theory]
    [InlineData("TOTRCDS(0)")]
    [InlineData("TOTRCDS(4294967289)")]
    [InlineData("TOTRCDS(-1)")]
    [InlineData("TOTRCDS(2.5)")]
    [InlineData("RECORDS(*BAD)")]
    [InlineData("MBR(*ALL)")]
    [InlineData("MBR(MISSING)")]
    [InlineData("UNKNOWN(1)")]
    public void Invalid_initialization_leaves_records_and_extent_unchanged(string option)
    {
        Add(99); Assert.True(new CommandService(_system).Execute("INZPFM FILE(QGPL/DATA) " + option).IsError);
        Assert.Equal(1, Extent()); Assert.Equal(99L, _files.ReadAll("QGPL", "DATA", "DATA").Single()["ID"]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_rejects_application_insert_triggers_including_quoted_names(bool deleted)
    {
        Sql("CREATE TRIGGER \"not AFTER INSERT\" /* INSERT */ BEFORE INSERT ON \"QGPL.DATA.DATA\" BEGIN SELECT 1; END");
        Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 2, deleted: deleted)); Assert.Equal(0, Extent());
    }
    [Fact]
    public void An_update_trigger_containing_INSERT_text_does_not_reject_initialization_or_execute()
    {
        Sql("CREATE TABLE effects(value TEXT); CREATE TRIGGER \"INSERT\" AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN INSERT INTO effects VALUES('AFTER INSERT'); END");
        Assert.Equal(2, _files.InitializeMember("QGPL", "DATA", totalRecords: 2)); Assert.Equal(0L, Sql("SELECT count(*) FROM effects"));
    }
    [Fact]
    public void Late_unique_failure_rolls_back_all_rows_extents_and_journal_entries()
    {
        var format = new RecordFormat { Name = "UREC", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 4, Sequence = 1 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "UNIQUE", new() { Name = "UNIQUE", Attribute = FileAttribute.Physical, Unique = true, Formats = [format] }, "");
        StartJournal("UNIQUE"); Assert.Throws<SqliteException>(() => _files.InitializeMember("QGPL", "UNIQUE", totalRecords: 2));
        Assert.Equal(0, _files.RowCount("QGPL", "UNIQUE", "UNIQUE")); Assert.Empty(_system.Journals.Read(new("QGPL", "JRN")));
        Assert.Equal(0L, Sql("SELECT high_water FROM sys_record_slots WHERE name='UNIQUE'"));
    }
    private void StartJournal(string file = "DATA")
    {
        _system.Journals.CreateReceiver(new("QGPL", "RCV")); _system.Journals.Create(new("QGPL", "JRN"), new("QGPL", "RCV"));
        _system.Journals.StartFile(new("QGPL", file), new("QGPL", "JRN"), "*BOTH");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_captures_one_atomic_F_IZ_event_and_resumes_ordinary_journaling(bool deleted)
    {
        StartJournal(); Assert.Equal(3, _files.InitializeMember("QGPL", "DATA", totalRecords: 3, deleted: deleted));
        var entry = Assert.Single(_system.Journals.Read(new("QGPL", "JRN"))); Assert.Equal("F", entry.Code); Assert.Equal("IZ", entry.Type); Assert.Equal(3, entry.RecordNumber);
        using var data = JsonDocument.Parse(entry.Data!); Assert.Equal("IPCINIT2", data.RootElement.GetProperty("Format").GetString()); Assert.Equal(deleted ? 1 : 0, data.RootElement.GetProperty("Flag").GetInt32());
        Assert.NotNull(entry.Schema); Assert.Equal(3, Extent());
        Add(9); Assert.Equal("PT", _system.Journals.Read(new("QGPL", "JRN"))[^1].Type); Assert.Equal(4, Extent());
    }
    [Fact]
    public void Failed_initialization_marker_rolls_back_default_records_and_bookkeeping()
    {
        StartJournal(); Sql("CREATE TRIGGER fail_init BEFORE INSERT ON sys_journal_entries WHEN NEW.entry_type='IZ' BEGIN SELECT RAISE(ABORT,'failure'); END");
        Assert.Throws<SqliteException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 3)); Assert.Equal(0, Extent()); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA")); Assert.Empty(_system.Journals.Read(new("QGPL", "JRN")));
    }
    [Fact]
    public void Compiled_CL_initializes_default_records_and_reopens_them_for_RCVF()
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "INIT", "PGM\nDCLF FILE(QGPL/DATA)\nINZPFM FILE(QGPL/DATA) TOTRCDS(2)\nRCVF\nSNDPGMMSG MSG(&LABEL)\nENDPGM", null);
        Command("CRTCLPGM PGM(QGPL/INIT) SRCFILE(QGPL/SOURCE)"); Command("CALL QGPL/INIT"); Assert.Equal(2, Extent());
    }
    [Fact]
    public void Resource_and_size_limits_fail_before_mutating_the_member()
    {
        Command("CHGPF FILE(QGPL/DATA) SIZE(*NOMAX)"); Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 1000001)); Assert.Equal(0, Extent());
        Command("CHGPF FILE(QGPL/DATA) SIZE(2 0 0)"); Assert.Throws<CpfException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 3, deleted: true)); Assert.Equal(0, Extent());
    }
}
