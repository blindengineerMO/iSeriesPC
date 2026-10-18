using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    private DatabaseRecordCursor Hold(ExecutionSession session, long rrn = 1)
    {
        using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var cursor = _files.OpenUpdateCursor("QGPL", "DATA", "DATA"); Assert.True(cursor.ChainRecord(rrn)); return cursor;
    }
    private async Task WaitForRecordWaiter(JobKey job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!Locks().Any(l => l.Waiting && l.Job == job))
        { Assert.True(DateTime.UtcNow < deadline, "RPG input must register a live record waiter."); await Task.Delay(20); }
    }
    [Theory]
    [InlineData("chain 20 DATA;")]
    [InlineData("setll 20 DATA;\nread DATA;")]
    [InlineData("setgt 20 DATA;\nreadp DATA;")]
    [InlineData("setll 20 DATA;\nreade 20 DATA;")]
    [InlineData("setgt 20 DATA;\nreadpe 20 DATA;")]
    public async Task OVRDBF_timed_waits_retrieve_fresh_records_for_every_update_input_operation(string operation)
    {
        Compile(UpdateFile() + operation + "\nsndpgmmsg %trim(TEXT);\nseton LR;");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(5) OVRSCOPE(*JOB)").IsError);
        var running = Task.Run(() => waiterSession.Execute("CALL QGPL/APP"));
        try
        {
            await WaitForRecordWaiter(waiterSession.Job.Key);
            using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Update(new Dictionary<string, object?> { ["TEXT"] = "fresh" });
            Ok(await running.WaitAsync(TimeSpan.FromSeconds(10)), "fresh");
        }
        finally { using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.DoesNotContain(Locks(), l => l.Waiting || l.Lifetime == "UpdateRead");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_attributes_and_indefinite_overrides_wait_without_truncating_to_the_old_service_limit(bool indefinite)
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, 32767);
        Compile(UpdateFile() + "chain 20 DATA;\nsndpgmmsg %char(ID);\nseton LR;");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        if (indefinite) Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(*NOMAX) OVRSCOPE(*JOB)").IsError);
        var running = Task.Run(() => waiterSession.Execute("CALL QGPL/APP"));
        try
        {
            await WaitForRecordWaiter(waiterSession.Job.Key);
            using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock();
            Ok(await running.WaitAsync(TimeSpan.FromSeconds(10)), "101");
        }
        finally { using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public void A_one_second_override_times_out_and_releases_the_failed_programs_waiter_and_target()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nupdate ROW;\nseton LR;");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(1)").IsError);
        var watch = Stopwatch.StartNew(); var result = waiterSession.Execute("CALL QGPL/APP");
        Assert.True(result.IsError); Assert.Contains("CPF1002", result.Message);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(700), "WAITRCD(1) must wait before reporting contention.");
        Assert.DoesNotContain(Locks(), l => l.Job == waiterSession.Job.Key && (l.Waiting || l.Lifetime == "UpdateRead"));
    }
    [Fact]
    public async Task An_indefinite_override_remains_cancellable_without_leaking_its_waiter()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nseton LR;");
        using var ownerSession = Session(); using var cancellation = new CancellationTokenSource();
        using var waiterSession = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), cancellation.Token); using var held = Hold(ownerSession);
        Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(*NOMAX)").IsError);
        var running = Task.Run(() => Record.Exception(() => waiterSession.Execute("CALL QGPL/APP")));
        try
        {
            await WaitForRecordWaiter(waiterSession.Job.Key); cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.DoesNotContain(Locks(), l => l.Job == waiterSession.Job.Key && (l.Waiting || l.Lifetime == "UpdateRead"));
    }
    [Fact]
    public void IMMED_overrides_a_nonzero_file_default_and_N_input_does_not_wait()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, 60);
        Compile(UpdateFile() + "chain 20 DATA;\nseton LR;", "LOCKED");
        Compile(UpdateFile() + "chain(n) 20 DATA;\nsndpgmmsg %char(ID);\nseton LR;", "INQUIRY");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(*IMMED)").IsError);
        var denied = waiterSession.Execute("CALL QGPL/LOCKED"); Assert.True(denied.IsError); Assert.Contains("CPF1002", denied.Message);
        Ok(waiterSession.Execute("CALL QGPL/INQUIRY"), "101"); Assert.DoesNotContain(Locks(), l => l.Waiting);
    }
    [Fact]
    public void SHARE_keeps_different_wait_policies_separate_and_reuses_compatible_positions()
    {
        Compile(UpdateFile() + "read(n) DATA;\nsndpgmmsg %char(ID);\nreturn;", "FIRST", "WAITFILES");
        Compile(UpdateFile() + "read(n) DATA;\nsndpgmmsg %char(ID);\nseton LR;", "SECOND");
        using var session = Session();
        Assert.False(session.Execute("OVRDBF FILE(DATA) SHARE(*YES) WAITRCD(1) OVRSCOPE(*JOB)").IsError); Ok(session.Execute("CALL QGPL/FIRST"), "102");
        Assert.False(session.Execute("OVRDBF FILE(DATA) SHARE(*YES) WAITRCD(2) OVRSCOPE(*JOB)").IsError); Ok(session.Execute("CALL QGPL/SECOND"), "102");
        Assert.False(session.Execute("OVRDBF FILE(DATA) SHARE(*YES) WAITRCD(1) OVRSCOPE(*JOB)").IsError); Ok(session.Execute("CALL QGPL/SECOND"), "101");
        Assert.False(session.Execute("RCLACTGRP ACTGRP(WAITFILES)").IsError);
    }
    [Theory]
    [InlineData("delete 20 DATA;")]
    [InlineData("delete 1 DATA;", false)]
    public async Task Key_and_RRN_DELETE_wait_for_their_selected_physical_record(string operation, bool keyed = true)
    {
        Compile("**free\ndcl-f DATA " + (keyed ? "keyed " : "") + "usage(*delete);\n" + operation + "\nsndpgmmsg 'DELETED';\nseton LR;");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        Assert.False(waiterSession.Execute("OVRDBF FILE(DATA) WAITRCD(5)").IsError);
        var running = Task.Run(() => waiterSession.Execute("CALL QGPL/APP"));
        try
        {
            await WaitForRecordWaiter(waiterSession.Job.Key);
            using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock();
            Ok(await running.WaitAsync(TimeSpan.FromSeconds(10)), "DELETED"); Assert.Equal(2, Count);
        }
        finally { using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public async Task Logical_input_uses_its_own_wait_attribute_and_locks_the_physical_source()
    {
        OutputView(); _files.ChangeLogicalRecordWait("QGPL", "VIEW", 5);
        Compile("**free\ndcl-f VIEW keyed usage(*update);\nchain 20 VIEW;\nsndpgmmsg %trim(TEXT);\nseton LR;");
        using var ownerSession = Session(); using var waiterSession = Session(); using var held = Hold(ownerSession);
        var running = Task.Run(() => waiterSession.Execute("CALL QGPL/APP"));
        try
        {
            await WaitForRecordWaiter(waiterSession.Job.Key);
            using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock();
            Ok(await running.WaitAsync(TimeSpan.FromSeconds(10)), "first");
        }
        finally { using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public async Task An_already_open_program_retains_its_wait_policy_after_the_file_attribute_changes()
    {
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, 5);
        Compile(UpdateFile() + "read DATA;\nsndpgmmsg %char(ID);\nreturn;", group: "WAITFILES");
        using var ownerSession = Session(); using var programSession = Session(); using var held = Hold(ownerSession, 1);
        Ok(programSession.Execute("CALL QGPL/APP"), "102"); _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, 0);
        var running = Task.Run(() => programSession.Execute("CALL QGPL/APP"));
        try
        {
            await WaitForRecordWaiter(programSession.Job.Key);
            using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock();
            Ok(await running.WaitAsync(TimeSpan.FromSeconds(10)), "101");
        }
        finally { using (OperationIdentity.Enter("QSECOFR", ownerSession.Job.Key)) held.Unlock(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.False(programSession.Execute("RCLACTGRP ACTGRP(WAITFILES)").IsError);
    }
}
