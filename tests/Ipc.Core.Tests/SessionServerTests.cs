using System.Net.Sockets;
using System.Buffers.Binary;
using System.Diagnostics;
using Ipc.Core.Security;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class SessionServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-server-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Submitted_CL_program_runs_in_the_server_with_durable_effects_and_its_own_job_log()
    {
        SeedUser();
        using (var setup = IpcSystem.Create(_directory))
        {
            setup.Start();
            setup.Objects.Create(new ObjectDescriptor
            {
                Key = new QualifiedName("QGPL", "BATCHPGM"), ObjectType = "*PGM", Attribute = "CLP",
                Source = "PGM\nCRTSRCPF FILE(QGPL/BATCHEFF)\nDSPJOB\nENDPGM",
            });
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            var result = await api.ExecuteAsync("SBMJOB CMD(CALL PGM(QGPL/BATCHPGM)) JOB(BATCHTEST)", stop.Token);
            Assert.True(result.Success, result.Result?.Message);
            using var factory = new SqliteConnectionFactory(_directory);
            var jobs = new JobService(factory);
            Job batch;
            do
            {
                await Task.Delay(25, stop.Token);
                batch = Assert.Single(jobs.List(), j => j.Key.Name == "BATCHTEST");
            } while (batch.Status != JobStatus.Completed);
            Assert.Equal(JobCompletion.Normal, batch.CompletionCode);
            Assert.Equal("TESTER", batch.UserProfile);
            Assert.NotEqual(api.Job, batch.Key);
            Assert.True(new SqliteObjectStore(factory).Exists("QGPL", "BATCHEFF", "*FILE"));
            Assert.Contains(jobs.GetLog(batch.Key), e => e.MessageType == "COMPLETION");
            var effect = Assert.Single(new Ipc.Services.Events.DurableEventStore(factory).Read("batch-audit", 1000).Events,
                e => e.Kind == "object.created" && e.Payload.Contains("BATCHEFF"));
            Assert.Equal("TESTER", effect.Principal);
            Assert.Equal(batch.Key.ToString(), effect.Job);
            await api.SignoffAsync(stop.Token);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Headless_clients_share_terminal_catalog_and_cannot_supply_a_different_job()
    {
        SeedUser();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            using var terminal = await Connect(server.SocketPath);
            await Read(terminal);
            await SignOn(terminal);
            Assert.True((await api.ExecuteAsync("CRTSRCPF FILE(QGPL/SHARED)", stop.Token)).Success);
            Assert.Contains("already exists", (await Command(terminal, "CRTSRCPF FILE(QGPL/SHARED)")).Text);
            var denied = await api.ExecuteAsync("DSPJOB JOB(OTHER)", stop.Token);
            Assert.False(denied.Success);
            Assert.Contains("IPC0003", denied.Result!.Message);
            var job = await api.ExecuteAsync("DSPJOB", stop.Token);
            Assert.Equal(api.Job, job.Job);
            Assert.Contains(api.Job.ToString(), job.Result!.Message);
            await api.SignoffAsync(stop.Token);
            using var factory = new SqliteConnectionFactory(_directory);
            Assert.Equal(JobCompletion.Normal, new JobService(factory).GetRequired(api.Job).CompletionCode);
            var audit = new Ipc.Services.Events.DurableEventStore(factory).Read("session-audit", 1000).Events;
            var effect = Assert.Single(audit, e => e.Kind == "object.created" && e.Payload.Contains("SHARED"));
            Assert.Equal("TESTER", effect.Principal);
            Assert.Equal(api.Job.ToString(), effect.Job);
            Assert.Contains(audit, e => e.Kind == "command.finished" && e.Job == api.Job.ToString());
            Assert.DoesNotContain(audit, e => e.Payload.Contains("PASS1234"));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData("TESTER", "WRONG")]
    [InlineData("MISSING", "PASS1234")]
    [InlineData("QSECOFR", "11111111")]
    public async Task Headless_authentication_denies_invalid_and_expired_credentials_without_creating_jobs(string user, string password)
    {
        SeedUser();
        if (user == "QSECOFR") password = File.ReadAllText(Path.Combine(_directory, "system.db.initial-password")).Trim();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CommandConnection.ConnectAsync(server.SocketPath, user, password, stop.Token));
            using var factory = new SqliteConnectionFactory(_directory);
            Assert.Empty(new JobService(factory).List());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Two_authenticated_sessions_share_catalog_with_independent_jobs_and_screens()
    {
        SeedUser();
        using var stop = new CancellationTokenSource();
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var first = await Connect(server.SocketPath);
            using var second = await Connect(server.SocketPath);
            var initial = await Read(first);
            await Read(second);
            Assert.Contains("User", initial.Text);
            await SignOn(first);
            await SignOn(second);
            using var factory = new SqliteConnectionFactory(_directory);
            var jobs = new JobService(factory);
            var active = jobs.List(JobStatus.Active, JobKeys.InteractiveSubsystem);
            Assert.Equal(2, active.Count);
            Assert.Equal(2, active.Select(j => j.Key.Number).Distinct().Count());

            var one = await Command(first, "DSPJOB");
            var two = await Command(second, "DSPJOB");
            Assert.NotEqual(one.Text, two.Text);
            Assert.Contains("Profile: TESTER", one.Text);
            Assert.Contains("Profile: TESTER", two.Text);
            var ended = await Send(first, new KeyPress(AidKey.Pf3));
            Assert.True(ended.Ended);
            // Server disposes the menu/job after writing the last frame.
            Assert.Null(await SessionWire.ReadAsync<TerminalFrame>(first));
            Assert.Single(jobs.List(JobStatus.Active, JobKeys.InteractiveSubsystem));
            Assert.Single(jobs.List(JobStatus.Completed, JobKeys.InteractiveSubsystem));
            Assert.False((await Command(second, "DSPJOB")).Ended);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var after = new SqliteConnectionFactory(_directory);
        Assert.Empty(new JobService(after).List(JobStatus.Active, JobKeys.InteractiveSubsystem));
        Assert.False(File.Exists(server.SocketPath));
    }

    [Fact]
    public async Task Bad_credentials_do_not_create_jobs_and_server_accepts_next_session()
    {
        SeedUser();
        using var stop = new CancellationTokenSource();
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var stream = await Connect(server.SocketPath);
            await Read(stream);
            await Type(stream, "TESTER");
            await Send(stream, new KeyPress(AidKey.None, CursorEdit.NextField));
            await Type(stream, "WRONGPW");
            Assert.True((await Send(stream, new KeyPress(AidKey.Enter))).Ended);
            using var factory = new SqliteConnectionFactory(_directory);
            Assert.Empty(new JobService(factory).List(JobStatus.Active));
            using var next = await Connect(server.SocketPath);
            Assert.Contains("User", (await Read(next)).Text);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Duplicate_server_is_rejected_and_shutdown_allows_restart()
    {
        SeedUser();
        using var stop = new CancellationTokenSource();
        var first = new SessionServer(_directory);
        var run = first.RunAsync(stop.Token);
        await first.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var duplicate = new SessionServer(_directory);
            await Assert.ThrowsAsync<IOException>(() => duplicate.RunAsync(stop.Token));
            await Assert.ThrowsAsync<IOException>(() => duplicate.Ready);
            using var client = await Connect(first.SocketPath);
            Assert.Contains("User", (await Read(client)).Text);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
        using var restartStop = new CancellationTokenSource();
        var restart = new SessionServer(_directory);
        var restarted = restart.RunAsync(restartStop.Token);
        await restart.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        restartStop.Cancel();
        await restarted.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Oversized_input_is_rejected_without_terminating_other_sessions()
    {
        SeedUser();
        using var stop = new CancellationTokenSource();
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var bad = await Connect(server.SocketPath);
            await Read(bad);
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, SessionWire.MaximumFrameBytes + 1);
            await bad.WriteAsync(size);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Null(await SessionWire.ReadAsync<TerminalFrame>(bad, timeout.Token));
            using var healthy = await Connect(server.SocketPath);
            Assert.Contains("User", (await Read(healthy)).Text);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Killed_server_releases_ownership_and_restart_marks_session_interrupted()
    {
        SeedUser();
        using var process = StartProcess();
        await WaitForReady(process);
        using (var client = await Connect(Path.Combine(_directory, "run", "as400.sock")))
        {
            await Read(client);
            await SignOn(client);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        using var restarted = StartProcess();
        try
        {
            await WaitForReady(restarted);
            using var factory = new SqliteConnectionFactory(_directory);
            var jobs = new JobService(factory);
            Assert.Empty(jobs.List(JobStatus.Active, JobKeys.InteractiveSubsystem));
            var recovered = Assert.Single(jobs.List(JobStatus.Completed, JobKeys.InteractiveSubsystem));
            Assert.Equal(JobCompletion.Abnormal, recovered.CompletionCode);
            Assert.Contains("Session server stopped", recovered.CompletionMessage);
            using var client = await Connect(Path.Combine(_directory, "run", "as400.sock"));
            await Read(client);
            await SignOn(client);
            var next = Assert.Single(jobs.List(JobStatus.Active, JobKeys.InteractiveSubsystem));
            Assert.True(next.Key.Number > recovered.Key.Number);
        }
        finally
        {
            if (!restarted.HasExited) restarted.Kill(entireProcessTree: true);
            await restarted.WaitForExitAsync();
        }
    }

    [Fact]
    public void Transport_redacts_hidden_fields()
    {
        var buffer = new DisplayBuffer();
        buffer.Write("secret", DisplayAttribute.NonDisplay);
        var frame = TerminalFrame.Capture(buffer);
        Assert.DoesNotContain("secret", frame.Text);
        Assert.Equal(' ', frame.ToBuffer()[1, 1].Value);
    }

    [Fact]
    public async Task Shutdown_cancels_executing_cl_program_and_records_abnormal_completion()
    {
        SeedUser();
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start();
            system.Objects.Create(new ObjectDescriptor
            {
                Key = new QualifiedName("QGPL", "SPIN"), ObjectType = ObjectType.Program, Attribute = "CLP",
                Source = "PGM\nCRTSRCPF FILE(QGPL/STARTED)\nSPIN:\nGOTO SPIN\nENDPGM",
            });
        }
        using var stop = new CancellationTokenSource();
        var server = new SessionServer(_directory);
        var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var stream = await Connect(server.SocketPath);
            await Read(stream);
            await SignOn(stream);
            await Type(stream, "CALL PGM(QGPL/SPIN)");
            await SessionWire.WriteAsync(stream, new TerminalInput(1, new KeyPress(AidKey.Enter)));
            using var factory = new SqliteConnectionFactory(_directory);
            var objects = new SqliteObjectStore(factory);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!objects.Exists("QGPL", "STARTED", ObjectType.File))
                await Task.Delay(10, timeout.Token);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            var job = Assert.Single(new JobService(factory).List(JobStatus.Completed, JobKeys.InteractiveSubsystem));
            Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
            Assert.Contains("interrupted", job.CompletionMessage);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Idle_communication_job_end_closes_its_connection_and_reclaims_the_job()
    {
        SeedUser(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            using var factory = new SqliteConnectionFactory(_directory); var jobs = new JobService(factory);
            var job = jobs.GetRequired(api.Job); Assert.Equal(JobType.Communication, job.Type); Assert.Equal("QSERVER", job.Subsystem);
            jobs.RequestEnd(job.Key);
            while ((job = jobs.GetRequired(job.Key)).ExecutionState == JobExecutionState.Running) await Task.Delay(20, stop.Token);
            Assert.Equal(JobExecutionState.Cancelled, job.ExecutionState);
            Assert.Empty(new JobInformationStore(factory).ActivationGroups(job.Key));
            await Assert.ThrowsAnyAsync<Exception>(() => api.ExecuteAsync("DSPJOB", stop.Token));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private Process StartProcess()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(Ipc.Server.Program).Assembly.Location);
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(_directory);
        return Process.Start(start)!;
    }

    private static async Task WaitForReady(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(line?.Contains("as400server ready:") == true,
            process.HasExited ? await process.StandardError.ReadToEndAsync() : "Server did not report readiness.");
    }

    private void SeedUser()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        system.Security.Profiles.Create(new UserProfile { Name = "TESTER" });
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("TESTER"), "PASS1234");
        system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "TESTER", Authorities.ChangeBits);
    }

    private static async Task<NetworkStream> Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static async Task<TerminalFrame> Read(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await SessionWire.ReadAsync<TerminalFrame>(stream, timeout.Token)
            ?? throw new IOException("Server ended unexpectedly.");
    }

    private static async Task<TerminalFrame> Send(Stream stream, KeyPress key)
    {
        await SessionWire.WriteAsync(stream, new TerminalInput(1, key));
        return await Read(stream);
    }

    private static async Task Type(Stream stream, string text)
    {
        foreach (var ch in text) await Send(stream, new KeyPress(AidKey.None, Character: ch));
    }

    private static async Task SignOn(Stream stream)
    {
        await Type(stream, "TESTER");
        await Send(stream, new KeyPress(AidKey.None, CursorEdit.NextField));
        await Type(stream, "PASS1234");
        var frame = await Send(stream, new KeyPress(AidKey.Enter));
        Assert.False(frame.Ended);
        Assert.Contains("Select one", frame.Text);
    }

    private static async Task<TerminalFrame> Command(Stream stream, string text)
    {
        await Type(stream, text);
        return await Send(stream, new KeyPress(AidKey.Enter));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
