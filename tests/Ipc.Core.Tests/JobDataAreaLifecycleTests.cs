using System.Diagnostics;
using System.Text.Json;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class JobDataAreaLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-job-areas-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = new();
    private static Job Running(IpcSystem system) => system.Jobs.CreateInteractive("QUSER");
    private static Job Routed(IpcSystem system, byte[]? initialization = null)
    {
        system.Jobs.Submit("ROUTED", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: initialization);
        return new BatchQueue(system.Connections, system.Jobs).ClaimNext()!.Value.Job;
    }
    private static long Count(IpcSystem system, string table)
    {
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM " + table;
        return (long)query.ExecuteScalar()!;
    }

    [Theory]
    [InlineData(37, 0x40)]
    [InlineData(500, 0x40)]
    [InlineData(1047, 0x40)]
    [InlineData(850, 0x20)]
    [InlineData(819, 0x20)]
    [InlineData(1208, 0x20)]
    [InlineData(367, 0x20)]
    public void New_local_and_group_areas_have_exact_character_lengths_and_job_encoded_blanks(int ccsid, byte blank)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var parent = system.Jobs.CreateInteractive("QUSER", ccsid: ccsid); var child = system.Jobs.CreateGroupJob(parent.Key, "CHILD");
        var local = areas.Read(parent.Key, JobDataArea.Local); var group = areas.Read(child.Key, JobDataArea.Group);
        Assert.Equal(1024, local.Length); Assert.Equal(512, group.Length); Assert.All(local.Concat(group), value => Assert.Equal(blank, value));
        Assert.Empty(system.Objects.Find("QGPL", "*", ObjectType.DataArea, null));
    }

    [Theory]
    [InlineData(JobDataArea.Local, 1024)]
    [InlineData(JobDataArea.Group, 512)]
    [InlineData(JobDataArea.Initialization, 2000)]
    public void Exact_byte_endpoints_are_mutable_and_invalid_ranges_never_change_storage(JobDataArea area, int length)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var job = area == JobDataArea.Initialization ? Routed(system) : Running(system);
        if (area == JobDataArea.Group) _ = system.Jobs.CreateGroupJob(job.Key, "CHILD");
        areas.Write(job.Key, area, 0, new byte[] { 0x00 }); areas.Write(job.Key, area, length - 1, new byte[] { 0xFF });
        var before = areas.Read(job.Key, area); Assert.Equal(0, before[0]); Assert.Equal(255, before[^1]);
        foreach (var range in new (int Offset, int Length)[] { (-1, 1), (length - 1, 2), (length, 1), (int.MaxValue, 1) })
        {
            Assert.Equal("IPC0126", Assert.Throws<CpfException>(() => areas.Write(job.Key, area, range.Offset, new byte[range.Length])).MessageId);
            Assert.Equal(before, areas.Read(job.Key, area));
        }
        areas.Write(job.Key, area, length, Array.Empty<byte>()); Assert.Equal(before, areas.Read(job.Key, area));
        before[0] = 0x77; Assert.Equal(0, areas.Read(job.Key, area)[0]);
    }

    [Theory]
    [InlineData("QUSER")]
    [InlineData("QSECOFR")]
    public void Even_same_profile_or_administrator_job_contexts_cannot_select_another_jobs_areas(string actor)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var owner = Routed(system); _ = system.Jobs.CreateGroupJob(Running(system).Key, "GROUP"); var stranger = system.Jobs.CreateInteractive(actor);
        using (OperationIdentity.Enter(actor, stranger.Key))
            foreach (var area in Enum.GetValues<JobDataArea>())
            {
                Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => areas.Read(owner.Key, area)).MessageId);
                Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => areas.Write(owner.Key, area, 0, new byte[] { 9 })).MessageId);
            }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1999)]
    [InlineData(2000)]
    [InlineData(2001)]
    public void Pending_initialization_is_copied_at_submission_and_materialized_once_at_claim(int? length)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var bytes = length is { } size ? Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray() : null; var expected = bytes?.ToArray();
        if (length > 2000)
        {
            Assert.Equal("IPC0126", Assert.Throws<CpfException>(() => system.Jobs.Submit("BAD", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: bytes)).MessageId);
            Assert.Equal(0, Count(system, "sys_job_environment")); return;
        }
        var job = system.Jobs.Submit("ROUTED", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: bytes);
        Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => areas.Read(job.Key, JobDataArea.Initialization)).MessageId);
        using (var connection = system.Connections.Open())
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT pip,pda FROM sys_job_environment WHERE job_number=$job"; query.Parameters.AddWithValue("$job", job.Key.Number);
            using var reader = query.ExecuteReader(); Assert.True(reader.Read()); Assert.True(reader.IsDBNull(1));
            if (bytes is null) Assert.True(reader.IsDBNull(0)); else Assert.Equal(expected, (byte[])reader.GetValue(0));
        }
        if (bytes?.Length > 0) bytes[0] = 0xEE;
        var queue = new BatchQueue(system.Connections, system.Jobs); Assert.Equal(job.Key, queue.ClaimNext()!.Value.Job.Key); Assert.Null(queue.ClaimNext());
        var data = areas.Read(job.Key, JobDataArea.Initialization); Assert.Equal(2000, data.Length);
        Assert.Equal(expected ?? Array.Empty<byte>(), data[..(length ?? 0)]); Assert.All(data[(length ?? 0)..], value => Assert.Equal(0x40, value));
        using var check = system.Connections.Open(); using var cleared = check.CreateCommand();
        cleared.CommandText = "SELECT pip IS NULL FROM sys_job_environment WHERE job_number=$job"; cleared.Parameters.AddWithValue("$job", job.Key.Number); Assert.Equal(1L, cleared.ExecuteScalar());
    }

    [Theory]
    [InlineData(JobCompletion.Normal)]
    [InlineData(JobCompletion.Abnormal)]
    public void Job_end_deletes_private_bytes_and_final_group_end_deletes_shared_bytes(JobCompletion completion)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var parent = Running(system); var child = system.Jobs.CreateGroupJob(parent.Key, "CHILD"); var routed = Routed(system, new byte[] { 1, 2 });
        areas.Write(parent.Key, JobDataArea.Group, 511, new byte[] { 0xAA });
        system.Jobs.Complete(parent.Key, completion); Assert.Equal(0xAA, areas.Read(child.Key, JobDataArea.Group)[511]); Assert.Equal(1, Count(system, "sys_job_groups"));
        system.Jobs.Complete(child.Key, completion); system.Jobs.Complete(routed.Key, completion);
        Assert.Equal(0, Count(system, "sys_job_groups")); Assert.Equal(0, Count(system, "sys_job_environment"));
        foreach (var area in Enum.GetValues<JobDataArea>()) Assert.Equal("CPF1241", Assert.Throws<CpfException>(() => areas.Read(routed.Key, area)).MessageId);
    }

    [Fact]
    public void Unavailable_group_and_initialization_areas_have_native_monitorable_errors()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var job = Running(system); var areas = new JobDataAreaStore(system.Connections);
        foreach (var (area, error) in new[] { (JobDataArea.Group, "CPF1046"), (JobDataArea.Initialization, "CPF1072") })
        {
            Assert.Equal(error, Assert.Throws<CpfException>(() => areas.Read(job.Key, area)).MessageId);
            Assert.Equal(error, Assert.Throws<CpfException>(() => areas.Write(job.Key, area, 0, new byte[] { 1 })).MessageId);
        }
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "JobAreaFixture44");
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "CHECKAREA"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = """
            PGM
            DCL &VALUE *CHAR LEN(1)
            RTVDTAARA DTAARA(*GDA (1 1)) RTNVAR(&VALUE)
            MONMSG CPF1046 EXEC(SNDPGMMSG MSG('NO GROUP'))
            RTVDTAARA DTAARA(*PDA (1 1)) RTNVAR(&VALUE)
            MONMSG CPF1072 EXEC(SNDPGMMSG MSG('NO ROUTING'))
            ENDPGM
            """ });
        using var session = new ExecutionSession(system, job, CancellationToken.None);
        var result = session.Execute("CALL CHECKAREA"); Assert.False(result.IsError, result.Message); Assert.Contains("NO GROUP", result.Message); Assert.Contains("NO ROUTING", result.Message);
    }

    [Fact]
    public void CL_group_jobs_share_GDA_but_keep_independent_LDA_and_maintain_storage_after_parent_end()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "JobAreaFixture44");
        using var parent = new ExecutionSession(system, Running(system), CancellationToken.None);
        using var child = new ExecutionSession(system, system.Jobs.CreateGroupJob(parent.Job.Key, "CHILD"), CancellationToken.None);
        var areas = new JobDataAreaStore(system.Connections);
        var changed = parent.Execute("CHGDTAARA DTAARA(*GDA (1 5)) VALUE('GROUP')"); Assert.False(changed.IsError, changed.Message);
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "GETGROUP"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nDCL &VALUE *CHAR LEN(5)\nRTVDTAARA DTAARA(*GDA (1 5)) RTNVAR(&VALUE)\nSNDPGMMSG MSG(&VALUE)\nENDPGM" });
        Assert.Contains("GROUP", child.Execute("CALL GETGROUP").Message);
        Assert.False(child.Execute("CHGDTAARA DTAARA(*LDA (1 5)) VALUE('CHILD')").IsError);
        Assert.All(areas.Read(parent.Job.Key, JobDataArea.Local), value => Assert.Equal(0x40, value));
        parent.End(JobCompletion.Normal, "parent done"); Assert.Contains("GROUP", child.Execute("CALL GETGROUP").Message);
        child.End(JobCompletion.Normal, "child done"); Assert.Equal(0, Count(system, "sys_job_groups"));
    }

    [Fact]
    public void Queued_LDA_and_PIP_survive_catalog_reopen_and_ended_parent_cleanup()
    {
        JobKey parent, child;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); parent = Running(system).Key; var areas = new JobDataAreaStore(system.Connections);
            areas.Write(parent, JobDataArea.Local, 1020, new byte[] { 0, 0x80, 0xFE, 0xFF });
            child = system.Jobs.Submit("CHILD", "", "QBATCH", profile: "QUSER", command: "DSPJOB", submittingJob: parent,
                initializationParameters: new byte[] { 0xC9, 0xD5, 0xC9, 0xE3 }).Key;
            areas.Write(parent, JobDataArea.Local, 1020, new byte[4]);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start(); reopened.Jobs.RecoverInterruptedInteractiveJobs();
        Assert.Equal(JobExecutionState.Interrupted, reopened.Jobs.GetRequired(parent).ExecutionState);
        var claimed = new BatchQueue(reopened.Connections, reopened.Jobs).ClaimNext()!.Value.Job; Assert.Equal(child, claimed.Key);
        var restored = new JobDataAreaStore(reopened.Connections); Assert.Equal(new byte[] { 0, 0x80, 0xFE, 0xFF }, restored.Read(child, JobDataArea.Local)[1020..]);
        Assert.Equal(new byte[] { 0xC9, 0xD5, 0xC9, 0xE3 }, restored.Read(child, JobDataArea.Initialization)[..4]);
        reopened.Security.Profiles.SetPassword(reopened.Security.Profiles.Get("QUSER"), "JobAreaFixture44");
        reopened.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "GETINIT"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nDCL &VALUE *CHAR LEN(4)\nRTVDTAARA DTAARA(*PDA (1 4)) RTNVAR(&VALUE)\nSNDPGMMSG MSG(&VALUE)\nENDPGM" });
        using var session = new ExecutionSession(reopened, claimed, CancellationToken.None); Assert.Contains("INIT", session.Execute("CALL GETINIT").Message);
        session.End(JobCompletion.Normal, "done"); Assert.Equal(0, Count(reopened, "sys_job_environment"));
    }

    [Fact]
    public void Failed_routing_claim_rolls_back_initialization_and_claim_identity_together()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var job = system.Jobs.Submit("CHILD", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: new byte[] { 0, 255 });
        using var connection = system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_pda BEFORE UPDATE OF pda ON sys_job_environment BEGIN SELECT RAISE(ABORT,'fixture'); END"; command.ExecuteNonQuery();
        var queue = new BatchQueue(system.Connections, system.Jobs); Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => queue.ClaimNext());
        Assert.Equal(JobExecutionState.Queued, system.Jobs.GetRequired(job.Key).ExecutionState); Assert.Equal(0, queue.Inspect(job.Key)!.Attempts);
        command.CommandText = "SELECT pip,pda FROM sys_job_environment";
        using (var reader = command.ExecuteReader()) { Assert.True(reader.Read()); Assert.Equal(new byte[] { 0, 255 }, (byte[])reader.GetValue(0)); Assert.True(reader.IsDBNull(1)); }
        command.CommandText = "DROP TRIGGER reject_pda"; command.ExecuteNonQuery(); Assert.Equal(job.Key, queue.ClaimNext()!.Value.Job.Key);
        Assert.Equal(new byte[] { 0, 255 }, new JobDataAreaStore(system.Connections).Read(job.Key, JobDataArea.Initialization)[..2]);
    }

    [Fact]
    public void Cancellation_and_crash_recovery_delete_pending_private_and_unreferenced_group_bytes()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var parent = Running(system); _ = system.Jobs.CreateGroupJob(parent.Key, "CHILD");
        var pending = system.Jobs.Submit("QUEUED", "", "QBATCH", profile: "QUSER", command: "DSPJOB", initializationParameters: new byte[] { 1 });
        system.Jobs.RequestEnd(pending.Key); Assert.Equal(JobExecutionState.Cancelled, system.Jobs.GetRequired(pending.Key).ExecutionState);
        var running = Routed(system, new byte[] { 2 });
        system.Jobs.RecoverInterruptedInteractiveJobs(); new BatchQueue(system.Connections, system.Jobs).RecoverInterrupted();
        Assert.Equal(JobExecutionState.Interrupted, system.Jobs.GetRequired(running.Key).ExecutionState);
        Assert.Equal(0, Count(system, "sys_job_environment")); Assert.Equal(0, Count(system, "sys_job_groups"));
    }

    [Fact]
    public void Cancelled_members_cannot_write_shared_storage_while_a_surviving_member_retains_it()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); var areas = new JobDataAreaStore(system.Connections);
        var parent = Running(system); var child = system.Jobs.CreateGroupJob(parent.Key, "CHILD");
        areas.Write(child.Key, JobDataArea.Group, 0, new byte[] { 0xAA }); system.Jobs.RequestEnd(parent.Key);
        Assert.Throws<OperationCanceledException>(() => areas.Read(parent.Key, JobDataArea.Local));
        Assert.Throws<OperationCanceledException>(() => areas.Write(parent.Key, JobDataArea.Group, 0, new byte[] { 0xBB }));
        Assert.Equal(0xAA, areas.Read(child.Key, JobDataArea.Group)[0]);
        system.Jobs.Complete(parent.Key); Assert.Equal(0xAA, areas.Read(child.Key, JobDataArea.Group)[0]);
        system.Jobs.Complete(child.Key); Assert.Equal(0, Count(system, "sys_job_groups"));
    }

    [Fact]
    public async Task Separate_processes_update_disjoint_group_ranges_without_losing_each_others_bytes()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "JobAreaFixture44");
        var parent = Running(system); var child = system.Jobs.CreateGroupJob(parent.Key, "CHILD"); var areas = new JobDataAreaStore(system.Connections);
        areas.Write(parent.Key, JobDataArea.Group, 0, Enumerable.Repeat((byte)0xCC, 512).ToArray());
        var writers = new[] { Start(parent.Key, 0), Start(child.Key, 496) };
        foreach (var process in writers)
        {
            await JobLockTests.WaitFor(() => File.Exists(Path.Combine(_directory, "ready-" + process.Id)) || process.HasExited);
            Assert.False(process.HasExited, process.HasExited ? await process.StandardError.ReadToEndAsync() : "");
        }
        File.WriteAllText(Path.Combine(_directory, "start"), "start");
        foreach (var process in writers)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            Assert.Equal(Enumerable.Repeat((byte)64, 16), JsonSerializer.Deserialize<byte[]>(await process.StandardOutput.ReadToEndAsync()));
        }
        var data = areas.Read(parent.Key, JobDataArea.Group); Assert.All(data[..16].Concat(data[496..]), value => Assert.Equal(64, value)); Assert.All(data[16..496], value => Assert.Equal(0xCC, value));
        system.Jobs.Complete(parent.Key); system.Jobs.Complete(child.Key); Assert.Equal(0, Count(system, "sys_job_groups"));
    }

    private Process Start(JobKey job, int offset)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { typeof(WorkFixture.Program).Assembly.Location, "job-area-write", _directory, "64", job.Number.ToString(), job.Name, job.User, offset.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start)!; _children.Add(process); return process;
    }
    public void Dispose()
    {
        foreach (var process in _children) { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } process.Dispose(); }
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
