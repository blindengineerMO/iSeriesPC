using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Session.Transport;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class JournalCommitRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-cycle-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;
    private readonly Job _job;
    private static readonly QualifiedName Journal = new("QGPL", "CYCLEJRN");
    private static readonly QualifiedName Parent = new("QGPL", "PARENT");
    private static readonly QualifiedName Other = new("QGPL", "OTHER");
    private static readonly JournalFileSelection[] Both = [new(Parent), new(Other)];
    public JournalCommitRecoveryTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "CycleRecoveryAdministrator31");
        _system.Journals.CreateReceiver(new("QGPL", "CYCLERCV1")); _system.Journals.Create(Journal, new("QGPL", "CYCLERCV1"));
        foreach (var name in new[] { Parent, Other })
        {
            var format = new RecordFormat { Name = "ROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8, Sequence = 1 }, new() { Name = "TEXT", Type = FieldType.Alpha, Length = 20 }] }; format.AssignPositions();
            _files.CreatePhysicalFile(name.Library, name.Name.Value, new() { Name = name.Name.Value, Attribute = FileAttribute.Physical, Formats = [format] }, "");
            _system.Journals.StartFile(name, Journal, "*BOTH");
            _files.Insert(name.Library, name.Name.Value, name.Name.Value, "ROW", new Dictionary<string, object?> { ["ID"] = 1L, ["TEXT"] = "base" });
        }
        _job = _system.Jobs.CreateInteractive("QSECOFR");
    }
    private object? Sql(string sql, bool replay = false)
    {
        using var connection = _system.Connections.Open(); if (replay) connection.CreateFunction("ipc_journal_replay", () => 1);
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private void Baseline() => Sql("UPDATE \"QGPL.PARENT.PARENT\" SET TEXT='base'; UPDATE \"QGPL.OTHER.OTHER\" SET TEXT='base'", true);
    private void AssertText(string parent, string other)
    { Assert.Equal(parent, Sql("SELECT TEXT FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(other, Sql("SELECT TEXT FROM \"QGPL.OTHER.OTHER\"")); }
    private (long First, long Last) Commit(string text = "committed")
    {
        var first = _system.Journals.Inspect(Journal).Sequence + 1;
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, Parent); _system.RecordTransactions.Enlist(_job.Key, Other);
        using (OperationIdentity.Enter("QSECOFR", _job.Key))
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            foreach (var name in new[] { "PARENT", "OTHER" }) statement.Execute("UPDATE \"QGPL." + name + "." + name + "\" SET TEXT=$text", new Dictionary<string, object?> { ["$text"] = text });
            statement.Complete();
        }
        _system.RecordTransactions.Commit(_job.Key); _system.RecordTransactions.End(_job.Key);
        return (first, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void A_complete_multifile_commit_cycle_removes_and_reapplies_without_duplicate_record_capture()
    {
        var range = Commit(); Assert.Equal((3L, 8L), range);
        var removed = _system.Journals.Recover(Journal, Both, range.Last, range.First, true);
        Assert.Equal(2, removed.Changes); Assert.Equal(6, removed.FirstSequence); Assert.Equal(4, removed.LastSequence); Assert.Null(removed.Boundaries); AssertText("base", "base");
        var applied = _system.Journals.Recover(Journal, Both, range.First, range.Last);
        Assert.Equal(2, applied.Changes); Assert.Equal(5, applied.FirstSequence); Assert.Equal(7, applied.LastSequence); AssertText("committed", "committed");
        Assert.Equal(6L, Sql("SELECT count(*) FROM sys_journal_entries WHERE code='R'"));
        Assert.Equal(8L, Sql("SELECT count(*) FROM sys_journal_entries WHERE code='F'"));
    }
    [Theory]
    [InlineData(false, 4)] [InlineData(false, 5)] [InlineData(false, 6)] [InlineData(false, 7)]
    [InlineData(true, 4)] [InlineData(true, 5)] [InlineData(true, 6)] [InlineData(true, 7)]
    public void A_start_inside_a_cycle_is_rejected_before_any_record_or_marker_changes(bool remove, long from)
    {
        Commit(); if (!remove) Baseline();
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, from, remove ? 3 : 8, remove));
        AssertText(remove ? "committed" : "base", remove ? "committed" : "base"); Assert.Equal(8, _system.Journals.Inspect(Journal).Sequence);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Either_control_marker_can_identify_a_boundary_without_replaying_the_adjacent_cycle(bool remove)
    {
        Commit("first"); Commit("second"); Sql("UPDATE \"QGPL.PARENT.PARENT\" SET TEXT='first'; UPDATE \"QGPL.OTHER.OTHER\" SET TEXT='first'", true);
        var result = _system.Journals.Recover(Journal, Both, remove ? 9 : 8, remove ? 3 : 14, remove);
        Assert.Equal(2, result.Changes); Assert.Null(result.Boundaries); AssertText(remove ? "base" : "second", remove ? "base" : "second");
    }
    [Fact]
    public void Apply_ending_inside_a_cycle_keeps_the_preceding_boundary_and_records_the_diagnostic()
    {
        Commit(); Sql("DELETE FROM \"QGPL.PARENT.PARENT\"; DELETE FROM \"QGPL.OTHER.OTHER\"", true);
        var result = _system.Journals.Recover(Journal, Both, 1, 7);
        Assert.Equal(2, result.Changes); Assert.Equal(1, result.FirstSequence); Assert.Equal(2, result.LastSequence); AssertText("base", "base");
        var boundary = Assert.Single(result.Boundaries!); Assert.Equal(3, boundary.StoppedBeforeSequence); Assert.Equal(7, boundary.RequestedEndSequence);
        foreach (var entry in _system.Journals.Read(Journal, 9).Where(entry => entry.Type == "AY"))
        { using var data = JsonDocument.Parse(entry.Data!); Assert.True(data.RootElement.GetProperty("IncompleteCommit").GetBoolean()); Assert.Equal(7, data.RootElement.GetProperty("To").GetInt64()); }
        Assert.Equal(1L, Sql("SELECT count(*) FROM sys_events WHERE kind='journal.recovery.boundary'"));
    }
    [Fact]
    public void A_range_containing_only_an_incomplete_cycle_changes_no_records_and_returns_a_diagnostic()
    {
        Commit(); Baseline(); var result = _system.Journals.Recover(Journal, Both, 3, 7);
        Assert.Equal(0, result.Changes); Assert.Equal(0, result.FirstSequence); Assert.Equal(0, result.LastSequence); Assert.Single(result.Boundaries!); AssertText("base", "base");
        Assert.Equal(8, _system.Journals.Inspect(Journal).Sequence); Assert.Equal(1L, Sql("SELECT count(*) FROM sys_events WHERE kind='journal.recovery.boundary'"));
    }
    [Fact]
    public void Reverse_recovery_stops_before_the_older_partial_cycle()
    {
        Commit("first"); Commit("second"); var result = _system.Journals.Recover(Journal, Both, 14, 4, true);
        Assert.Equal(2, result.Changes); Assert.Equal(12, result.FirstSequence); Assert.Equal(10, result.LastSequence); AssertText("first", "first");
        var boundary = Assert.Single(result.Boundaries!); Assert.Equal(8, boundary.StoppedBeforeSequence); Assert.Equal(4, boundary.RequestedEndSequence);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Explicit_no_boundary_mode_accepts_an_individual_record_endpoint(bool remove)
    {
        Commit(); if (!remove) Baseline();
        var sequence = remove ? 4 : 5;
        var result = _system.Journals.Recover(Journal, [new(Parent)], sequence, sequence, remove, honorCommitBoundaries: false);
        Assert.Equal(1, result.Changes); Assert.Null(result.Boundaries); AssertText(remove ? "base" : "committed", remove ? "committed" : "base");
    }
    [Theory]
    [InlineData(JournalRecoveryErrorPolicy.End)] [InlineData(JournalRecoveryErrorPolicy.Continue)] [InlineData(JournalRecoveryErrorPolicy.Atomic)]
    public void Native_record_errors_keep_the_successful_prefix_even_when_it_is_inside_a_commit_cycle(JournalRecoveryErrorPolicy policy)
    {
        Commit(); Baseline(); Sql("DELETE FROM \"QGPL.OTHER.OTHER\"", true);
        if (policy == JournalRecoveryErrorPolicy.Atomic)
        {
            Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, 3, 8, errorPolicy: policy));
            Assert.Equal("base", Sql("SELECT TEXT FROM \"QGPL.PARENT.PARENT\"")); Assert.Equal(8, _system.Journals.Inspect(Journal).Sequence);
        }
        else
        {
            var result = _system.Journals.Recover(Journal, Both, 3, 8, errorPolicy: policy); Assert.Equal(1, result.Changes);
            Assert.Equal(7, Assert.Single(result.Failures!).Sequence); Assert.Equal("committed", Sql("SELECT TEXT FROM \"QGPL.PARENT.PARENT\""));
        }
        Assert.Equal(0L, Sql("SELECT count(*) FROM \"QGPL.OTHER.OTHER\""));
    }
    [Fact]
    public void An_earlier_END_error_does_not_report_an_ending_commit_boundary_as_reached()
    {
        Commit("first"); Commit("second"); Baseline(); Sql("DELETE FROM \"QGPL.PARENT.PARENT\"", true);
        var result = _system.Journals.Recover(Journal, Both, 3, 13, errorPolicy: JournalRecoveryErrorPolicy.End);
        Assert.Equal(0, result.Changes); Assert.Single(result.Failures!); Assert.Null(result.Boundaries);
        Assert.Equal(0L, Sql("SELECT count(*) FROM sys_events WHERE kind='journal.recovery.boundary'"));
    }
    [Theory]
    [InlineData("user_data=X'7B'", 3)]
    [InlineData("user_data=CAST(user_data AS TEXT)", 3)]
    [InlineData("job=CAST(job AS BLOB)", 3)]
    [InlineData("job='foreign'", 5)]
    [InlineData("commit_cycle=NULL", 5)]
    [InlineData("before_image=NULL", 5)]
    [InlineData("record_number=1.5", 5)]
    [InlineData("commit_cycle=CAST(commit_cycle AS BLOB)", 5)]
    [InlineData("sequence='invalid'", 8)]
    [InlineData("entry_type='RB'", 8)]
    [InlineData("user_data=CAST('{\"Format\":\"IPCCMT1\",\"Definition\":\"00000000000000000000000000000000\",\"Cycle\":\"00000000000000000000000000000000\"}' AS BLOB)", 8)]
    public void Corrupt_or_unsupported_cycle_metadata_cannot_be_silently_applied(string assignment, int sequence)
    {
        Commit(); Baseline(); Sql("UPDATE sys_journal_entries SET " + assignment + " WHERE sequence=" + sequence);
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, 3, 8)); AssertText("base", "base"); Assert.Equal(8, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void A_record_with_a_stripped_tag_cannot_hide_an_endpoint_inside_a_cycle()
    {
        Commit(); Baseline(); Sql("UPDATE sys_journal_entries SET commit_cycle=NULL WHERE sequence=5");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, [new(Parent)], 5, 5)); AssertText("base", "base");
    }
    [Fact]
    public void Forced_commitment_before_images_do_not_remove_the_native_AFTER_only_file_restriction()
    {
        _system.Journals.EndFile(Parent); _system.Journals.StartFile(Parent, Journal, "*AFTER"); Commit();
        Assert.Contains(_system.Journals.Read(Journal), entry => entry.ObjectName == "PARENT" && entry.Type == "UB");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, 8, 3, true)); AssertText("committed", "committed");
        Baseline(); Assert.Equal(2, _system.Journals.Recover(Journal, Both, 3, 8).Changes); AssertText("committed", "committed");
    }
    [Fact]
    public void Recovery_is_rejected_if_the_executing_job_has_an_unrelated_commitment_definition()
    {
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, Other);
        using (OperationIdentity.Enter("QSECOFR", _job.Key)) Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, [new(Parent)], 1, 1, true));
        AssertText("base", "base"); Assert.Equal(2, _system.Journals.Inspect(Journal).Sequence); _system.RecordTransactions.End(_job.Key);
    }
    [Fact]
    public void Trusted_recovery_also_rejects_a_file_with_a_live_commitment_allocation()
    {
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, Parent);
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, [new(Parent)], 1, 1, true)).MessageId);
        AssertText("base", "base"); Assert.Equal(2, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public void A_later_allocation_failure_releases_previously_acquired_recovery_leases()
    {
        Commit(); Baseline(); var job = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        using var held = locks.Acquire(_job.Key, new("QGPL", "PARENT", ObjectType.File), JobLockMode.SharedRead, TimeSpan.Zero);
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, 3, 8));
        Assert.DoesNotContain(locks.Inspect(new("QGPL", "OTHER", ObjectType.File)), holder => holder.Lifetime == "JournalRecovery");
        AssertText("base", "base"); Assert.Equal(8, _system.Journals.Inspect(Journal).Sequence);
    }
    [Fact]
    public async Task Exclusive_recovery_allocations_survive_the_writer_wait_and_release_on_cancellation()
    {
        Commit(); Baseline(); var job = _system.Jobs.CreateInteractive("QSECOFR"); var locks = new JobLockStore(_system.Connections);
        using (var connection = _system.Connections.Open()) using (var transaction = connection.BeginTransaction(deferred: false))
        using (var stop = new CancellationTokenSource())
        {
            var recovery = Task.Run(() => { using var identity = OperationIdentity.Enter("QSECOFR", job.Key); return _system.Journals.Recover(Journal, Both, 3, 8, cancellationToken: stop.Token); });
            try
            {
                await JobLockTests.WaitFor(() => locks.Inspect(new("QGPL", "PARENT", ObjectType.File)).Any(holder => holder.Lifetime == "JournalRecovery") || recovery.IsCompleted);
                Assert.False(recovery.IsCompleted);
                Assert.Throws<CpfException>(() => locks.Acquire(_job.Key, new("QGPL", "PARENT", ObjectType.File), JobLockMode.SharedRead, TimeSpan.Zero));
            }
            finally { stop.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => recovery.WaitAsync(TimeSpan.FromSeconds(4))); }
        }
        Assert.DoesNotContain(locks.Inspect(new("QGPL", "PARENT", ObjectType.File)), holder => holder.Lifetime == "JournalRecovery"); AssertText("base", "base");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cycle_lookahead_stays_within_the_authorized_receiver_range(bool remove)
    {
        Commit("first"); _system.Journals.CreateReceiver(new("QGPL", "CYCLERCV2")); _system.Journals.Rotate(Journal, new("QGPL", "CYCLERCV2")); Commit("second");
        if (!remove) Sql("UPDATE \"QGPL.PARENT.PARENT\" SET TEXT='first'; UPDATE \"QGPL.OTHER.OTHER\" SET TEXT='first'", true);
        foreach (var file in new[] { Parent, Other }) _system.Security.Authority.Grant(file.Library, file.Name.Value, ObjectType.File, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "CYCLEJRN", ObjectType.Journal, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "CYCLERCV1", ObjectType.JournalReceiver, "QUSER", AuthorityBit.None);
        _system.Security.Authority.Grant("QGPL", "CYCLERCV2", ObjectType.JournalReceiver, "QUSER", Authorities.UseBits);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal(2, _system.Journals.Recover(Journal, Both, remove ? 14 : 9, remove ? 9 : 14, remove).Changes);
        AssertText(remove ? "first" : "second", remove ? "first" : "second");
    }
    [Fact]
    public void A_receiver_owned_by_another_journal_cannot_supply_a_cycle_record()
    {
        Commit(); Baseline(); _system.Journals.CreateReceiver(new("QGPL", "FOREIGNRCV")); _system.Journals.Create(new("QGPL", "FOREIGNJRN"), new("QGPL", "FOREIGNRCV"));
        Sql("UPDATE sys_journal_entries SET receiver_id=(SELECT id FROM sys_journal_receivers WHERE name='FOREIGNRCV') WHERE sequence=5 AND journal_id=(SELECT id FROM sys_journals WHERE name='CYCLEJRN')");
        Assert.Throws<CpfException>(() => _system.Journals.Recover(Journal, Both, 3, 8)); AssertText("base", "base");
    }
    [Fact]
    public void More_than_eight_cycles_recover_without_retaining_all_cycle_metadata()
    {
        for (var index = 1; index <= 12; index++) Commit(index.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        Baseline(); var result = _system.Journals.Recover(Journal, Both, 3, 74);
        Assert.Equal(24, result.Changes); AssertText("12", "12");
        Assert.Equal(24, _system.Journals.Recover(Journal, Both, 74, 3, true).Changes); AssertText("base", "base");
    }
    [Fact]
    public void A_shared_cycle_identifier_in_two_journals_is_recovered_using_each_journals_own_range()
    {
        var second = new QualifiedName("QGPL", "SECONDJRN"); _system.Journals.CreateReceiver(new("QGPL", "SECONDRCV")); _system.Journals.Create(second, new("QGPL", "SECONDRCV"));
        _system.Journals.EndFile(Other); _system.Journals.StartFile(Other, second, "*BOTH"); var first = Commit();
        Assert.Equal((3L, 6L), first); Assert.Equal(4, _system.Journals.Inspect(second).Sequence);
        Assert.Equal(_system.Journals.Read(Journal).Single(entry => entry.Type == "SC").CommitCycle, _system.Journals.Read(second).Single(entry => entry.Type == "SC").CommitCycle);
        Assert.Equal(1, _system.Journals.Recover(Journal, [new(Parent)], 6, 3, true).Changes); AssertText("base", "committed");
        Assert.Equal(1, _system.Journals.Recover(second, [new(Other)], 4, 1, true).Changes); AssertText("base", "base");
        _system.Journals.Recover(Journal, [new(Parent)], 3, 6); _system.Journals.Recover(second, [new(Other)], 1, 4); AssertText("committed", "committed");
    }
    [Fact]
    public void Native_commands_surface_boundary_diagnostics_and_accept_explicit_NO()
    {
        Commit(); Baseline(); var commands = new CommandService(_system, _job);
        var partial = commands.Execute("APYJRNCHG JRN(QGPL/CYCLEJRN) FILE((QGPL/PARENT) (QGPL/OTHER)) RCVRNG(*CURRENT) FROMENT(3) TOENT(7)");
        Assert.False(partial.IsError, partial.Message); Assert.Contains("Commitment boundary", partial.Message); Assert.Contains(partial.Listing!, line => line.Contains("stopped before sequence 3")); AssertText("base", "base");
        var individual = commands.Execute("APYJRNCHG JRN(QGPL/CYCLEJRN) FILE(QGPL/PARENT) RCVRNG(*CURRENT) FROMENT(5) TOENT(5) CMTBDY(*NO)");
        Assert.False(individual.IsError, individual.Message); AssertText("committed", "base");
    }
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Compiled_CL_applies_and_removes_the_record_units_cycle_in_its_authenticated_job(int ccsid)
    {
        Commit(); _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240); var compiler = new CommandService(_system);
        foreach (var remove in new[] { true, false })
        {
            var name = remove ? "CYCLERMV" : "CYCLEAPY";
            var command = (remove ? "RMVJRNCHG" : "APYJRNCHG") + " JRN(QGPL/CYCLEJRN) FILE((QGPL/PARENT) (QGPL/OTHER)) RCVRNG(*CURRENT) FROMENT(" + (remove ? 8 : 3) + ") TOENT(" + (remove ? 3 : 8) + ")";
            _files.SaveSourceMember("QGPL", "SOURCE", name, "PGM\n" + command + "\nENDPGM", null);
            var compiled = compiler.Execute("CRTCLPGM PGM(QGPL/" + name + ") SRCFILE(QGPL/SOURCE)"); Assert.False(compiled.IsError, compiled.Message);
        }
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid); using var session = new ExecutionSession(_system, job, CancellationToken.None);
        var removed = session.Execute("CALL QGPL/CYCLERMV"); Assert.False(removed.IsError, removed.Message); AssertText("base", "base");
        var applied = session.Execute("CALL QGPL/CYCLEAPY"); Assert.False(applied.IsError, applied.Message); AssertText("committed", "committed");
        Assert.All(_system.Journals.Read(Journal).Where(entry => entry.Code == "F"), entry => Assert.Equal(job.Key.ToString(), entry.Job));
    }
    [Fact]
    public async Task Independent_socket_jobs_receive_cycle_diagnostics_and_recheck_receiver_authority()
    {
        Commit(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"), "CycleSocketUser31");
        foreach (var file in new[] { Parent, Other }) _system.Security.Authority.Grant(file.Library, file.Name.Value, ObjectType.File, "QUSER", Authorities.AllBits);
        _system.Security.Authority.Grant("QGPL", "CYCLEJRN", ObjectType.Journal, "QUSER", Authorities.AllBits);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token);
        const string selection = " JRN(QGPL/CYCLEJRN) FILE((QGPL/PARENT) (QGPL/OTHER)) RCVRNG(*CURRENT)";
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            await using var first = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "CycleSocketUser31", stop.Token);
            await using var second = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "CycleSocketUser31", stop.Token);
            var result = await first.ExecuteAsync("RMVJRNCHG" + selection + " FROMENT(8) TOENT(3)", stop.Token); Assert.True(result.Success, result.Result?.Message); AssertText("base", "base");
            Assert.Equal(first.Job.ToString(), _system.Journals.Read(Journal)[^1].Job);
            result = await second.ExecuteAsync("APYJRNCHG" + selection + " FROMENT(3) TOENT(7)", stop.Token); Assert.True(result.Success, result.Result?.Message);
            Assert.Contains(result.Result!.Listing!, line => line.Contains("Incomplete commit cycle")); AssertText("base", "base");
            Assert.Equal(second.Job.ToString(), Sql("SELECT job FROM sys_events WHERE kind='journal.recovery.boundary' ORDER BY seq DESC LIMIT 1"));
            result = await second.ExecuteAsync("APYJRNCHG" + selection + " FROMENT(3) TOENT(8)", stop.Token); Assert.True(result.Success, result.Result?.Message); AssertText("committed", "committed");
            _system.Security.Authority.Grant("QGPL", "CYCLERCV1", ObjectType.JournalReceiver, "QUSER", AuthorityBit.None);
            result = await first.ExecuteAsync("RMVJRNCHG" + selection + " FROMENT(8) TOENT(3)", stop.Token); Assert.False(result.Success); Assert.Equal("CPF9802", result.Result?.MessageId); AssertText("committed", "committed");
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public void A_real_record_unit_cycle_larger_than_the_display_page_streams_through_recovery()
    {
        _system.RecordTransactions.Begin(_job.Key); _system.RecordTransactions.Enlist(_job.Key, Parent);
        using (var statement = _system.RecordTransactions.Open(_job.Key))
        {
            for (var index = 1; index <= 10005; index++)
                statement.Execute("UPDATE \"QGPL.PARENT.PARENT\" SET TEXT=$text", new Dictionary<string, object?> { ["$text"] = index.ToString(global::System.Globalization.CultureInfo.InvariantCulture) });
            statement.Complete();
        }
        _system.RecordTransactions.Commit(_job.Key); _system.RecordTransactions.End(_job.Key);
        var last = _system.Journals.Inspect(Journal).Sequence; Assert.Equal(20014, last);
        Assert.Equal(10005, _system.Journals.Recover(Journal, [new(Parent)], last, 3, true).Changes); AssertText("base", "base");
        Assert.Equal(10005, _system.Journals.Recover(Journal, [new(Parent)], 3, last).Changes); AssertText("10005", "base");
    }
    public void Dispose() { _system.Dispose(); try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
