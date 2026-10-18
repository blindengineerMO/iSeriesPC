using Ipc.Core.Messages;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Terminal;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-journals-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;
    private static readonly QualifiedName Journal = new("QGPL", "TESTJRN");
    private static readonly QualifiedName Receiver = new("QGPL", "RCV0001");
    private static readonly QualifiedName File = new("QGPL", "RECORDS");
    public JournalTests()
    {
        _system = IpcSystem.Create(_directory, "system.db"); _system.Start();
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "JournalTests30");
        _files = new(_system.Connections, _system.Objects);
        _system.Journals.CreateReceiver(Receiver); _system.Journals.Create(Journal, Receiver);
    }
    private void CreateFile(string images = "*BOTH")
    {
        var format = new RecordFormat { Name = "REC", Fields = [
            new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100, NullCapable = true }] };
        format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "RECORDS", new() { Name = "RECORDS", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        _system.Journals.StartFile(File, Journal, images);
    }
    private void Insert(long id, string? text = "first", string member = "RECORDS") =>
        _files.Insert("QGPL", "RECORDS", member, "REC", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = text });
    private object? Sql(string sql, SqliteTransaction? transaction = null)
    {
        using var connection = transaction is null ? _system.Connections.Open() : null;
        using var command = (transaction?.Connection ?? connection!).CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command.ExecuteScalar();
    }
    [Fact]
    public void User_entries_survive_restart_and_rotation_retains_continuous_sequences()
    {
        Assert.Equal(1, _system.Journals.Send(Journal, "aa", [0, 255, 1]));
        var next = _system.Journals.Rotate(Journal); Assert.Equal(new QualifiedName("QGPL", "RCV0002"), next);
        Assert.Equal(2, _system.Journals.Send(Journal, "BB", [2]));
        using var reopened = IpcSystem.Create(_directory, "system.db"); reopened.Start();
        var entries = reopened.Journals.Read(Journal); Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.Sequence));
        Assert.Equal("AA", entries[0].Type); Assert.Equal(new byte[] { 0, 255, 1 }, entries[0].Data);
        Assert.Equal(Receiver, entries[0].Receiver); Assert.Equal(next, entries[1].Receiver);
        Assert.Single(reopened.Journals.Read(Journal, 2, maximum: 1));
        Assert.Equal(2, reopened.Journals.Receivers(Journal).Count);
        Assert.False(reopened.Journals.InspectReceiver(Receiver).Attached);
    }
    [Fact]
    public void Receiver_deletion_requires_detachment_oldest_order_and_explicit_unsaved_acknowledgement()
    {
        _system.Journals.Send(Journal, "AA", [1]);
        Assert.Throws<CpfException>(() => _system.Journals.DeleteReceiver(Receiver, true));
        var second = _system.Journals.Rotate(Journal); _system.Journals.Rotate(Journal);
        Assert.Throws<CpfException>(() => _system.Journals.DeleteReceiver(second, true));
        Assert.Throws<CpfException>(() => _system.Journals.DeleteReceiver(Receiver));
        _system.Journals.DeleteReceiver(Receiver, true); Assert.Empty(_system.Journals.Read(Journal));
        _system.Journals.Delete(Journal); Assert.False(_system.Journals.InspectReceiver(new("QGPL", "RCV0003")).Attached);
        _system.Journals.DeleteReceiver(second, true); _system.Journals.DeleteReceiver(new("QGPL", "RCV0003"), true);
        Assert.Equal(0L, Sql("SELECT count(*) FROM sys_journal_receivers"));
    }
    [Fact]
    public void Failed_rotation_and_duplicate_creation_leave_no_partial_descriptors_or_chain_changes()
    {
        _system.Journals.CreateReceiver(new("QGPL", "RCV0002"));
        Assert.Throws<CpfException>(() => _system.Journals.Rotate(Journal));
        Assert.Equal(Receiver, _system.Journals.Inspect(Journal).Receiver);
        Assert.Null(_system.Journals.InspectReceiver(new("QGPL", "RCV0002")).AttachedAt);
        Assert.Throws<CpfException>(() => _system.Journals.Create(new("QGPL", "OTHERJRN"), Receiver));
        Assert.False(_system.Objects.Exists("QGPL", "OTHERJRN", ObjectType.Journal));
    }
    [Fact]
    public void Record_changes_capture_exact_images_and_new_members_in_the_same_transaction()
    {
        CreateFile(); Insert(long.MaxValue);
        _files.Update("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = long.MaxValue, ["TEXT"] = "changed" });
        _files.Delete("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = long.MaxValue });
        _files.AddMember("QGPL", "RECORDS", "SECOND"); Insert(2, null, "SECOND");
        var entries = _system.Journals.Read(Journal);
        Assert.Equal(new[] { "PT", "UB", "UP", "DL", "PT" }, entries.Select(e => e.Type));
        Assert.Equal(long.MaxValue.ToString(), entries[0].After!.Single(c => c.Name == "ID").Value);
        Assert.Equal("first", entries[1].Before!.Single(c => c.Name == "TEXT").Value);
        Assert.Equal("changed", entries[2].After!.Single(c => c.Name == "TEXT").Value);
        Assert.Equal("changed", entries[3].Before!.Single(c => c.Name == "TEXT").Value);
        Assert.Equal("SECOND", entries[4].Member); Assert.Null(entries[4].After!.Single(c => c.Name == "TEXT").Value);
        Assert.All(entries, e => { Assert.Equal("R", e.Code); Assert.Equal(1, e.RecordNumber); Assert.NotNull(e.Schema); });
        Assert.Throws<CpfException>(() => _system.Journals.Delete(Journal));
        _system.Journals.EndFile(File); Insert(3); Assert.Equal(5, _system.Journals.Read(Journal).Count);
        _system.Journals.Delete(Journal);
    }
    [Fact]
    public void Rollback_and_receiver_capacity_failures_roll_back_data_sequence_and_accounting()
    {
        CreateFile();
        using (var connection = _system.Connections.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Sql("INSERT INTO \"QGPL.RECORDS.RECORDS\"(ID,TEXT) VALUES(1,'uncommitted')", transaction);
            Assert.Equal(1L, Sql("SELECT sequence FROM sys_journals", transaction)); transaction.Rollback();
        }
        Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS"));
        Assert.Equal(0, _system.Journals.Inspect(Journal).Sequence); Assert.Equal(0, _system.Journals.InspectReceiver(Receiver).Bytes);
        Sql("UPDATE sys_journal_receivers SET bytes=1073741824");
        Assert.Throws<SqliteException>(() => Insert(2));
        Assert.Equal(0, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(0, _system.Journals.Inspect(Journal).Sequence);
        _system.Journals.Rotate(Journal); Insert(3); Assert.Single(_system.Journals.Read(Journal));
    }
    [Fact]
    public void Schema_and_record_number_changes_fail_closed_until_journaling_is_ended()
    {
        CreateFile(); Insert(1);
        Assert.Throws<SqliteException>(() => Sql("UPDATE \"QGPL.RECORDS.RECORDS\" SET rowid=2"));
        Sql("ALTER TABLE \"QGPL.RECORDS.RECORDS\" ADD COLUMN EXTRA TEXT");
        Assert.Throws<SqliteException>(() => Insert(2)); Assert.Single(_system.Journals.Read(Journal));
        _system.Journals.EndFile(File); Insert(2);
    }
    [Fact]
    public void After_images_omit_update_before_entries_and_threshold_is_not_a_hard_data_limit()
    {
        CreateFile("*AFTER"); Insert(1);
        _files.Update("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "second" });
        Assert.Equal(new[] { "PT", "UP" }, _system.Journals.Read(Journal).Select(e => e.Type));
        Assert.Null(_system.Journals.Read(Journal)[1].Before);
        _system.Journals.CreateReceiver(new("QGPL", "SMALL"), 1);
        Assert.Equal(100000, _system.Journals.InspectReceiver(new("QGPL", "SMALL")).ThresholdKilobytes);
        _system.Journals.CreateReceiver(new("QGPL", "UNBOUNDED"), null);
        Assert.Null(_system.Journals.InspectReceiver(new("QGPL", "UNBOUNDED")).ThresholdKilobytes);
        _system.Journals.Rotate(Journal, new("QGPL", "SMALL"));
        Sql("UPDATE sys_journal_receivers SET bytes=102400001 WHERE name='SMALL'");
        _system.Journals.Send(Journal, "AA", new byte[3000]);
        Assert.True(_system.Journals.InspectReceiver(new("QGPL", "SMALL")).Bytes > 100000 * 1024);
        Assert.Throws<CpfException>(() => _system.Journals.Send(Journal, "AA", new byte[3001]));
    }
    [Fact]
    public void Live_receiver_authority_blocks_reading_but_does_not_require_file_writers_to_read_journals()
    {
        CreateFile(); var job = _system.Jobs.CreateInteractive("QUSER");
        _system.Security.Authority.Grant("QGPL", "RECORDS", ObjectType.File, "QUSER", Authorities.ChangeBits);
        _system.Security.Authority.Grant("QGPL", "TESTJRN", ObjectType.Journal, "QUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "RCV0001", ObjectType.JournalReceiver, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER", job.Key))
        {
            Insert(1); Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _system.Journals.Read(Journal)).MessageId);
            Assert.Throws<CpfException>(() => _system.Journals.Rotate(Journal));
            Assert.Throws<CpfException>(() => _system.Journals.EndFile(File));
        }
        _system.Security.Authority.Grant("QGPL", "RCV0001", ObjectType.JournalReceiver, "QUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER", job.Key))
        { var entry = Assert.Single(_system.Journals.Read(Journal)); Assert.Equal("QUSER", entry.Principal); Assert.NotNull(entry.Job); }
        _system.Security.Authority.Grant("QGPL", "TESTJRN", ObjectType.Journal, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER", job.Key)) Assert.Throws<CpfException>(() => _system.Journals.Read(Journal));
    }
    [Fact]
    public void Copy_excludes_only_managed_hooks_and_rename_keeps_bindings_and_history_identity()
    {
        CreateFile(); Insert(1);
        Sql("CREATE TABLE user_capture(n INTEGER); CREATE TRIGGER ipc_jrn_user AFTER INSERT ON \"QGPL.RECORDS.RECORDS\" BEGIN INSERT INTO user_capture VALUES(NEW.ID); END");
        _system.ObjectOperations.Relocate(File, ObjectType.File, new("QGPL", "COPY"), copy: true);
        _files.Insert("QGPL", "COPY", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 2L });
        Assert.Single(_system.Journals.Read(Journal)); Assert.Equal(2L, Sql("SELECT n FROM user_capture"));
        _system.ObjectOperations.Relocate(File, ObjectType.File, new("QGPL", "RENAMED"));
        _files.Insert("QGPL", "RENAMED", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 3L });
        Assert.Equal("RENAMED", _system.Journals.Read(Journal)[1].ObjectName);
        Assert.NotNull(_system.Journals.Read(Journal)[0].BindingId);
        Assert.Equal(_system.Journals.Read(Journal)[0].BindingId, _system.Journals.Read(Journal)[1].BindingId);
        _system.ObjectOperations.Relocate(Journal, ObjectType.Journal, new("QGPL", "NEWJRN"));
        _system.ObjectOperations.Relocate(Receiver, ObjectType.JournalReceiver, new("QGPL", "NEWRCV"));
        Assert.Equal(new QualifiedName("QGPL", "NEWRCV"), _system.Journals.Inspect(new("QGPL", "NEWJRN")).Receiver);
        Assert.Equal(2, _system.Journals.Read(new("QGPL", "NEWJRN")).Count);
        Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QGPL", "NEWJRN"), ObjectType.Journal, new("QGPL", "CLONE"), copy: true));
        _files.RemoveMember("QGPL", "RENAMED", "RECORDS"); Assert.Equal(0L, Sql("SELECT count(*) FROM sys_journal_triggers"));
        _files.DeleteFile("QGPL", "RENAMED"); Assert.Equal(0L, Sql("SELECT count(*) FROM sys_journal_bindings"));
    }
    [Fact]
    public void Direct_SQL_images_preserve_binary_null_text_integer_and_real_bits()
    {
        CreateFile();
        Sql("INSERT INTO \"QGPL.RECORDS.RECORDS\"(ID,TEXT) VALUES(9223372036854775807,X'00FF')");
        Sql("INSERT INTO \"QGPL.RECORDS.RECORDS\"(ID,TEXT) VALUES(1.0000000000000002,NULL)");
        var entries = _system.Journals.Read(Journal);
        Assert.Equal(new("TEXT", "blob", "00FF"), entries[0].After!.Single(c => c.Name == "TEXT"));
        Assert.Equal(new("ID", "real", "3FF0000000000001"), entries[1].After!.Single(c => c.Name == "ID"));
        Assert.Equal(new("TEXT", "null", null), entries[1].After!.Single(c => c.Name == "TEXT"));
    }
    [Fact]
    public void SQL_replace_captures_its_delete_and_insert_and_ignore_cannot_bypass_sequence_exhaustion()
    {
        CreateFile(); Insert(1);
        Sql("INSERT OR REPLACE INTO \"QGPL.RECORDS.RECORDS\"(rowid,ID,TEXT) VALUES(1,2,'replacement')");
        Assert.Equal(new[] { "PT", "DL", "PT" }, _system.Journals.Read(Journal).Select(e => e.Type));
        Sql("UPDATE sys_journals SET sequence=9223372036854775807");
        Assert.Throws<SqliteException>(() => Sql("INSERT OR IGNORE INTO \"QGPL.RECORDS.RECORDS\"(ID,TEXT) VALUES(3,'ignored capture')"));
        Assert.Equal(1, _files.RowCount("QGPL", "RECORDS", "RECORDS")); Assert.Equal(3, _system.Journals.Read(Journal).Count);
    }
    [Fact]
    public void Commands_and_compiled_CL_use_the_shared_store_with_binary_data_and_checked_parameters()
    {
        var commands = new CommandService(_system);
        void Run(string line) { var result = commands.Execute(line); Assert.False(result.IsError, result.Message); }
        Run("CRTJRNRCV JRNRCV(QGPL/CMD0001) THRESHOLD(*NONE)");
        Assert.True(commands.Execute("CRTJRN JRN(QGPL/CMDJRN) JRNRCV(QGPL/CMD0001)").IsError);
        Run("CRTJRN JRN(QGPL/CMDJRN) JRNRCV(QGPL/CMD0001) MNGRCV(*USER)");
        _files.CreateSourceFile("QGPL", "SOURCE");
        _files.SaveSourceMember("QGPL", "SOURCE", "SENDJRN", "PGM\nDCL &DATA *CHAR LEN(5) VALUE(X'00FF014040')\nSNDJRNE JRN(QGPL/CMDJRN) TYPE(AA) ENTDTA(&DATA)\nENDPGM", null);
        Run("CRTCLPGM PGM(QGPL/SENDJRN) SRCFILE(QGPL/SOURCE) SRCMBR(SENDJRN)"); Run("CALL QGPL/SENDJRN");
        Assert.Equal(new byte[] { 0, 255, 1, 64, 64 }, Assert.Single(_system.Journals.Read(new("QGPL", "CMDJRN"))).Data);
        Run("SNDJRNE JRN(QGPL/CMDJRN) TYPE(01) ENTDTA(0001)");
        Run("SNDJRNE JRN(QGPL/CMDJRN) TYPE(02) ENTDTA('*BLANK')");
        Run("SNDJRNE JRN(QGPL/CMDJRN) TYPE(03) ENTDTA(*BLANK)");
        var text = Ipc.Core.Text.CodePage.FromCcsid(_system.Config.Ccsid);
        var sent = _system.Journals.Read(new("QGPL", "CMDJRN"));
        Assert.Equal(text.GetBytes("0001"), sent[1].Data); Assert.Equal(text.GetBytes("*BLANK"), sent[2].Data); Assert.Empty(sent[3].Data!);
        var display = commands.Execute("DSPJRN JRN(QGPL/CMDJRN) NBRENT(1)"); Assert.False(display.IsError, display.Message);
        Assert.Contains("00FF01", string.Join(" ", Assert.Single(display.WorkList!.Rows).Actions.Single().Details!));
        Run("CHGJRN JRN(QGPL/CMDJRN) JRNRCV(*GEN)"); Run("DLTJRNRCV JRNRCV(QGPL/CMD0001) DLTOPT(*IGNINQMSG)");
        Run("DLTJRN JRN(QGPL/CMDJRN)"); Run("DLTJRNRCV JRNRCV(QGPL/CMD0002) DLTOPT(*IGNINQMSG)");
    }
    [Theory]
    [InlineData("SNDJRNE JRN(QGPL/TESTJRN) TYPE(A)")]
    [InlineData("CRTJRNRCV JRNRCV(QGPL/NOPE) THRESHOLD(0)")]
    [InlineData("DSPJRN JRN(QGPL/TESTJRN) FROMENT(0)")]
    [InlineData("DSPJRN JRN(QGPL/TESTJRN) NBRENT(4001)")]
    [InlineData("DSPJRN JRN(QGPL/TESTJRN) OUTPUT(*OUTFILE)")]
    [InlineData("STRJRNPF FILE(QGPL/TESTJRN) JRN(QGPL/TESTJRN) OMTJRNE(*OPNCLO)")]
    public void Unsupported_command_options_fail_without_creating_payloads(string command)
    {
        Assert.True(new CommandService(_system).Execute(command).IsError);
        Assert.Equal(1L, Sql("SELECT count(*) FROM sys_journal_receivers")); Assert.Empty(_system.Journals.Read(Journal));
    }
    [Fact]
    public void Journal_entries_open_in_the_terminal_detail_view_and_return()
    {
        _system.Journals.Send(Journal, "AA", [0, 255]);
        using var menu = new MenuController(_system, _system.Security.Profiles.Get("QSECOFR"));
        foreach (var c in "DSPJRN JRN(QGPL/TESTJRN)") menu.Handle(new(AidKey.None, Character: c));
        Assert.Equal("WorkWith", menu.Handle(new(AidKey.Enter)).State);
        menu.Handle(new(AidKey.None, Character: '5')); menu.Handle(new(AidKey.Enter));
        Assert.Contains("00FF", string.Concat(menu.Buffer.Positions().Select(p => menu.Buffer[p.Row, p.Column].Value)));
        menu.Handle(new(AidKey.Pf3)); menu.Handle(new(AidKey.Pf3)); Assert.Contains("Selection", menu.Buffer.RowText(23));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Compiled_CL_user_entries_preserve_all_byte_values_and_trailing_job_spaces(int ccsid)
    {
        var bytes = Enumerable.Range(0, 256).Select(n => (byte)n).Concat(Enumerable.Repeat(ccsid == 37 ? (byte)64 : (byte)32, 2)).ToArray();
        ClExternalCallTests.Create(_system, "SENDALL", "CLP", "PGM\nDCL &BYTES *CHAR LEN(258) VALUE(X'" + Convert.ToHexString(bytes) + "')\nSNDJRNE JRN(QGPL/TESTJRN) ENTDTA(&BYTES)\nENDPGM");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid);
        using var session = new Ipc.Session.ExecutionSession(_system, job, CancellationToken.None);
        var result = session.Execute("CALL QGPL/SENDALL"); Assert.False(result.IsError, result.Message);
        var entry = Assert.Single(_system.Journals.Read(Journal)); Assert.Equal(bytes, entry.Data); Assert.Equal(job.Key.ToString(), entry.Job);
    }
    [Fact]
    public void Source_save_installs_hooks_before_loading_new_members_and_copy_into_target_is_captured()
    {
        _files.CreateSourceFile("QGPL", "SOURCE"); _system.Journals.StartFile(new("QGPL", "SOURCE"), Journal);
        var first = _files.SaveSourceMember("QGPL", "SOURCE", "NEW", "one\ntwo", null);
        _files.SaveSourceMember("QGPL", "SOURCE", "NEW", "changed", first.Revision);
        Assert.Equal(new[] { "PT", "PT", "DL", "DL", "PT" }, _system.Journals.Read(Journal).Select(e => e.Type));
        Assert.All(_system.Journals.Read(Journal), e => Assert.Equal("NEW", e.Member));
        var commands = new CommandService(_system);
        var copy = commands.Execute("CPYF FROMFILE(QGPL/SOURCE) TOFILE(QGPL/SOURCE) FROMMBR(NEW) TOMBR(SOURCE) MBROPT(*ADD)");
        Assert.False(copy.IsError, copy.Message); Assert.Equal("SOURCE", _system.Journals.Read(Journal)[^1].Member);
    }
    [Fact]
    public void Failure_on_the_second_update_image_rolls_back_the_first_image_and_the_record()
    {
        CreateFile(); Insert(1);
        Sql("CREATE TRIGGER fail_up BEFORE INSERT ON sys_journal_entries WHEN NEW.entry_type='UP' BEGIN SELECT RAISE(ABORT,'injected'); END");
        Assert.Throws<SqliteException>(() => _files.Update("QGPL", "RECORDS", "RECORDS", "REC", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "changed" }));
        Assert.Single(_system.Journals.Read(Journal)); Assert.Equal(1, _system.Journals.Inspect(Journal).Sequence);
        Assert.Equal("first", Sql("SELECT TEXT FROM \"QGPL.RECORDS.RECORDS\""));
    }
    [Fact]
    public async Task Cancellation_while_waiting_for_a_writer_leaves_no_entry_or_sequence_gap()
    {
        using var connection = _system.Connections.Open(); using var held = connection.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource();
        var sending = Task.Run(() => _system.Journals.Send(Journal, "AA", [1], stop.Token));
        await Task.Delay(100); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(4)));
        held.Rollback(); Assert.Empty(_system.Journals.Read(Journal)); Assert.Equal(1, _system.Journals.Send(Journal, "AA", [2]));
    }
    [Fact]
    public void Exclusive_allocation_blocks_foreign_journal_changes_and_deletion()
    {
        var holder = _system.Jobs.CreateInteractive("QSECOFR"); var contender = _system.Jobs.CreateInteractive("QSECOFR");
        using var allocation = new JobLockStore(_system.Connections).Acquire(holder.Key, new("QGPL", "TESTJRN", ObjectType.Journal), JobLockMode.Exclusive, TimeSpan.Zero);
        using (OperationIdentity.Enter("QSECOFR", contender.Key))
        using (new JobLockStore(_system.Connections).EnterCommand(contender.Key))
        { Assert.Throws<CpfException>(() => _system.Journals.Send(Journal, "AA", [1])); Assert.Throws<CpfException>(() => _system.Journals.Rotate(Journal)); Assert.Throws<CpfException>(() => _system.Journals.Delete(Journal)); }
        Assert.Empty(_system.Journals.Read(Journal));
    }
    [Fact]
    public async Task Independent_process_writers_allocate_unique_contiguous_durable_sequences()
    {
        var children = new List<global::System.Diagnostics.Process>();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var start = new global::System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { typeof(WorkFixture.Program).Assembly.Location, "journal-append", _directory, "20" }) start.ArgumentList.Add(argument);
                children.Add(global::System.Diagnostics.Process.Start(start)!);
            }
            foreach (var child in children) await JobLockTests.WaitFor(() => global::System.IO.File.Exists(Path.Combine(_directory, "ready-" + child.Id)) || child.HasExited);
            global::System.IO.File.WriteAllText(Path.Combine(_directory, "start"), "start");
            foreach (var child in children)
            { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync()); }
            var sequences = _system.Journals.Read(Journal).Select(e => e.Sequence).ToArray();
            Assert.Equal(Enumerable.Range(1, 40).Select(n => (long)n), sequences);
            Assert.Equal(40, _system.Journals.InspectReceiver(Receiver).LastSequence);
        }
        finally { foreach (var child in children) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } child.Dispose(); } }
    }
    public void Dispose() { _system.Dispose(); try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
