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
    public async Task Native_crypto_contexts_survive_calls_but_cannot_be_shared_between_socket_jobs_and_are_released_at_signoff()
    {
        SeedUser();
        using (var setup = IpcSystem.Create(_directory))
        {
            setup.Start(); ClExternalCallTests.Create(setup, "SOCKKEY", "CLP", """
                PGM
                DCL &TOKEN *CHAR LEN(8)
                CALL QSYS/QC3CRTKX PARM(X'0000000000000000000000000000000000000000000000000000000000000000' (32 (*INT 4)) '0' (3 (*INT 4)) '0' '' '' &TOKEN X'00000000')
                CHGVAR %SST(*LDA 1 8) &TOKEN
                ENDPGM
                """);
            ClExternalCallTests.Create(setup, "SOCKHMAC", "CLP", """
                PGM
                DCL &TOKEN *CHAR LEN(8)
                DCL &HASH *CHAR LEN(32)
                CHGVAR &TOKEN %SST(*LDA 1 8)
                CALL QSYS/QC3CALHM PARM('' (0 (*INT 4)) 'DATA0100' (3 (*INT 4)) 'ALGD0500' &TOKEN 'KEYD0100' '1' '' &HASH X'00000000')
                SNDPGMMSG MSG('SOCKET CRYPTO ACCEPTED')
                ENDPGM
                """);
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var first = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            await using var second = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            var created = await Task.WhenAll(first.ExecuteAsync("CALL QGPL/SOCKKEY", stop.Token), second.ExecuteAsync("CALL QGPL/SOCKKEY", stop.Token));
            Assert.All(created, result => Assert.True(result.Success, result.Result?.Message));
            var used = await Task.WhenAll(first.ExecuteAsync("CALL QGPL/SOCKHMAC", stop.Token), second.ExecuteAsync("CALL QGPL/SOCKHMAC", stop.Token));
            Assert.All(used, result => { Assert.True(result.Success, result.Result?.Message); Assert.Contains("SOCKET CRYPTO ACCEPTED", result.Result!.Message); });
            using var factory = new SqliteConnectionFactory(_directory); var areas = new JobDataAreaStore(factory);
            var firstToken = areas.Read(first.Job, JobDataArea.Local)[..8]; var secondToken = areas.Read(second.Job, JobDataArea.Local)[..8]; Assert.NotEqual(firstToken, secondToken);
            areas.Write(second.Job, JobDataArea.Local, 0, firstToken);
            var denied = await second.ExecuteAsync("CALL QGPL/SOCKHMAC", stop.Token); Assert.False(denied.Success); Assert.Equal("CPF9DF5", denied.Result!.MessageId);
            Assert.True((await first.ExecuteAsync("CALL QGPL/SOCKHMAC", stop.Token)).Success);
            await first.SignoffAsync(stop.Token); await second.SignoffAsync(stop.Token);
            using var connection = factory.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM sys_crypto_keys"; Assert.Equal(0L, query.ExecuteScalar());
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Native_job_lists_and_ID_retrieval_stay_bound_to_independent_socket_jobs()
    {
        SeedUser();
        using (var setup = IpcSystem.Create(_directory))
        {
            setup.Start(); ClExternalCallTests.Create(setup, "SOCKJOBS", "CLP", """
                PGM
                DCL &LIST *CHAR LEN(512)
                DCL &INFO *CHAR LEN(86)
                DCL &ID *CHAR LEN(16)
                CALL QSYS/QUSCRTUS PARM('JOBS      QTEMP     ' '' (512 (*INT 4)) X'FE' '*CHANGE' ('Socket job list' (*CHAR 50)))
                CALL QSYS/QUSLJOB PARM('JOBS      QTEMP     ' 'JOBL0100' '*' '*ACTIVE' X'00000000')
                CALL QSYS/QUSRTVUS PARM('JOBS      QTEMP     ' (1 (*INT 4)) (512 (*INT 4)) &LIST X'00000000')
                CHGVAR &ID %SST(&LIST 415 16)
                CALL QSYS/QUSRJOBI PARM(&INFO (86 (*INT 4)) 'JOBI0100' '*INT' &ID X'00000000')
                IF (%BIN(&LIST 133 4) *EQ 1 *AND %SST(&INFO 61 2) *EQ 'BD') THEN(SNDPGMMSG MSG(%SST(&INFO 29 6)))
                ENDPGM
                """);
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var running = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var first = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            await using var second = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            var calls = await Task.WhenAll(first.ExecuteAsync("CALL QGPL/SOCKJOBS", stop.Token), second.ExecuteAsync("CALL QGPL/SOCKJOBS", stop.Token));
            Assert.True(calls[0].Success, calls[0].Result?.Message); Assert.True(calls[1].Success, calls[1].Result?.Message);
            Assert.Equal(first.Job.Number.ToString("D6"), calls[0].Result!.Message!.Trim()); Assert.Equal(second.Job.Number.ToString("D6"), calls[1].Result!.Message!.Trim());
            Assert.NotEqual(first.Job, second.Job); await first.SignoffAsync(stop.Token); await second.SignoffAsync(stop.Token);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Native_system_value_readers_observe_authorized_changes_from_an_independent_socket_job()
    {
        SeedUser();
        using (var setup = IpcSystem.Create(_directory))
        {
            setup.Start(); setup.Security.Profiles.SetPassword(setup.Security.Profiles.Get("QSECOFR"), "NativeSysval27");
            ClExternalCallTests.Create(setup, "READSVAL", "CLP", """
                PGM
                DCL &INFO *CHAR LEN(28)
                CALL QSYS/QWCRSVAL PARM(&INFO (28 (*INT 4)) (1 (*INT 4)) 'QCCSID' X'00000000')
                IF (%BIN(&INFO 25 4) *EQ 37) THEN(SNDPGMMSG MSG('ORIGINAL VALUE ACCEPTED'))
                IF (%BIN(&INFO 25 4) *EQ 1208) THEN(SNDPGMMSG MSG('CHANGED VALUE ACCEPTED'))
                ENDPGM
                """);
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var reader = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            await using var admin = await CommandConnection.ConnectAsync(server.SocketPath, "QSECOFR", "NativeSysval27", stop.Token);
            var original = await reader.ExecuteAsync("CALL QGPL/READSVAL", stop.Token);
            Assert.True(original.Success, original.Result?.Message); Assert.Contains("ORIGINAL VALUE ACCEPTED", original.Result!.Message);
            var changed = await admin.ExecuteAsync("CHGSYSVAL SYSVAL(QCCSID) VALUE(1208)", stop.Token); Assert.True(changed.Success, changed.Result?.Message);
            var denied = await reader.ExecuteAsync("CHGSYSVAL SYSVAL(QCCSID) VALUE(37)", stop.Token); Assert.False(denied.Success);
            var current = await reader.ExecuteAsync("CALL QGPL/READSVAL", stop.Token);
            Assert.True(current.Success, current.Result?.Message); Assert.Contains("CHANGED VALUE ACCEPTED", current.Result!.Message);
            var list = await reader.ExecuteAsync("WRKSYSVAL SYSVAL(QCCSID)", stop.Token); Assert.True(list.Success, list.Result?.Message);
            Assert.Contains(list.Result!.WorkList!.Rows, row => row.Text.Contains("1208", StringComparison.Ordinal));
            await reader.SignoffAsync(stop.Token); await admin.SignoffAsync(stop.Token);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Independent_socket_jobs_deliver_to_a_native_allocated_message_receive()
    {
        SeedUser();
        using (var setup = IpcSystem.Create(_directory))
        {
            setup.Start(); new Ipc.Services.Messages.MessageQueueStore(setup.Connections).Create(new("QGPL", "INBOX"));
            ClExternalCallTests.Create(setup, "WAITMSG", "CLP", """
                PGM
                DCL &INFO *CHAR LEN(128)
                CALL QSYS/QMHRCVM PARM(&INFO (128 (*INT 4)) 'RCVM0100' 'INBOX     QGPL      ' '*ANY' '' (-1 (*INT 4)) '*REMOVE' X'00000000')
                IF (%SST(&INFO 49 5) *EQ 'READY') THEN(SNDPGMMSG MSG('NATIVE SOCKET RECEIVE ACCEPTED'))
                ENDPGM
                """);
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var receiver = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            await using var sender = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            using var factory = new SqliteConnectionFactory(_directory);
            long ReceiverCount()
            {
                using var connection = factory.Open(); using var query = connection.CreateCommand();
                query.CommandText = "SELECT count(*) FROM sys_message_receivers"; return (long)query.ExecuteScalar()!;
            }
            var waiting = receiver.ExecuteAsync("CALL QGPL/WAITMSG", stop.Token);
            while (ReceiverCount() == 0) await Task.Delay(10, stop.Token);
            var conflict = await sender.ExecuteAsync("CALL QGPL/WAITMSG", stop.Token);
            Assert.False(conflict.Success); Assert.Equal("CPF2451", conflict.Result!.MessageId);
            var sent = await sender.ExecuteAsync("CALL QSYS/QMHSNDM PARM('' '' 'READY' (5 (*INT 4)) '*INFO' 'INBOX     QGPL      ' (1 (*INT 4)) '' '' X'00000000')", stop.Token);
            Assert.True(sent.Success, sent.Result?.Message);
            var received = await waiting; Assert.True(received.Success, received.Result?.Message);
            Assert.Contains("NATIVE SOCKET RECEIVE ACCEPTED", received.Result!.Message); Assert.Equal(0, ReceiverCount());
            Assert.Empty(new Ipc.Services.Messages.MessageQueueStore(factory).List(new("QGPL", "INBOX")));
            Assert.DoesNotContain(new JobLockStore(factory).Inspect(new("QGPL", "INBOX", ObjectType.MessageQueue)), l => l.Lifetime == "MessageReceive");
            await receiver.SignoffAsync(stop.Token); await sender.SignoffAsync(stop.Token);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compiled_display_exchange_over_the_terminal_socket_is_isolated_between_jobs(bool rpg)
    {
        SeedDisplayProgram(rpg: rpg); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            using var first = await Connect(server.SocketPath); await Read(first); await SignOn(first);
            using var second = await Connect(server.SocketPath); await Read(second); await SignOn(second);
            var firstFrame = await Command(first, "CALL QGPL/DISPLAY"); var secondFrame = await Command(second, "CALL QGPL/DISPLAY");
            Assert.Equal("ProgramDisplay", firstFrame.State); Assert.Equal("ProgramDisplay", secondFrame.State);
            Assert.Contains("START", firstFrame.Text); Assert.Contains("START", secondFrame.Text);
            await Send(first, new(AidKey.None, CursorEdit.FieldEraseToEnd)); await Type(first, "ALPHA");
            var returned = await Send(first, new(AidKey.Enter)); Assert.Equal("Menu", returned.State);
            await Send(second, new(AidKey.None, CursorEdit.FieldEraseToEnd)); await Type(second, "BETA");
            returned = await Send(second, new(AidKey.Enter)); Assert.Equal("Menu", returned.State);
            using var factory = new SqliteConnectionFactory(_directory); var jobs = new JobService(factory).List().Where(j => j.Type == JobType.Interactive).OrderBy(j => j.Key.Number).ToArray();
            Assert.Equal(2, jobs.Length);
            foreach (var (job, expected) in jobs.Zip(new[] { "ALPHA", "BETA" }))
            {
                using var identity = Ipc.Services.Events.OperationIdentity.Enter("TESTER", job.Key);
                Assert.Equal(expected, Assert.IsType<ProgramBuffer>(new DataAreaStore(factory).Read("QTEMP", "RESULT").Value).ToText().TrimEnd());
            }
            Assert.True((await Send(first, new(AidKey.Pf3))).Ended); Assert.True((await Send(second, new(AidKey.Pf3))).Ended);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Disconnect_or_ENDJOB_cancels_a_display_wait_before_job_cleanup(bool endJob, bool rpg)
    {
        SeedDisplayProgram(marker: true, rpg: rpg); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(stop.Token);
        try
        {
            using var terminal = await Connect(server.SocketPath); await Read(terminal); await SignOn(terminal);
            Assert.Equal("ProgramDisplay", (await Command(terminal, "CALL QGPL/DISPLAY")).State);
            using var factory = new SqliteConnectionFactory(_directory); var jobs = new JobService(factory); var job = Assert.Single(jobs.List());
            if (endJob) jobs.RequestEnd(job.Key); else terminal.Dispose();
            while ((job = jobs.GetRequired(job.Key)).Status != JobStatus.Completed) await Task.Delay(20, stop.Token);
            Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
            if (endJob) Assert.Equal(JobExecutionState.Cancelled, job.ExecutionState);
            using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", job.Key.Number);
            Assert.Equal(0L, query.ExecuteScalar());
            Assert.False(new SqliteObjectStore(factory).Exists("QGPL", "UNREACHED", ObjectType.DataArea));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private void SeedDisplayProgram(bool marker = false, bool rpg = false)
    {
        SeedUser(); using var setup = IpcSystem.Create(_directory); setup.Start();
        new Ipc.Dsp.DisplayFileStore(setup.Connections).Create("QGPL", "SCREEN", DisplayPanelTests.Dds(keywords: "CA03(03)") +
            DisplayPanelTests.Dds("ENTRY", record: true) + DisplayPanelTests.Dds("NAME", 10, row: 4, column: 2, usage: 'B'));
        var source = new Ipc.Db.Store.SqliteFileStore(setup.Connections, setup.Objects); source.CreateSourceFile("QGPL", "SOURCE");
        var resultSource = "PGM PARM(&NAME)\nDCL &NAME *CHAR LEN(10)\nCRTDTAARA DTAARA(QTEMP/RESULT) TYPE(*CHAR) LEN(10) VALUE(&NAME)" +
            (marker ? "\nCRTDTAARA DTAARA(QGPL/UNREACHED) TYPE(*CHAR) LEN(10) VALUE(&NAME)" : "") + "\nENDPGM";
        source.SaveSourceMember("QGPL", "SOURCE", "SAVE", resultSource, null);
        var commands = new Ipc.Console.Session.CommandService(setup);
        var save = commands.Execute("CRTCLPGM PGM(QGPL/SAVE) SRCFILE(QGPL/SOURCE)"); Assert.False(save.IsError, save.Message);
        source.SaveSourceMember("QGPL", "SOURCE", "DISPLAY", rpg ?
            "**free\ndcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc);\neval NAME = 'START';\nexfmt ENTRY;\ncallp SAVE(NAME);\nseton LR;" :
            "DCLF QGPL/SCREEN\nCHGVAR &NAME 'START'\nSNDRCVF\nCRTDTAARA DTAARA(QTEMP/RESULT) TYPE(*CHAR) LEN(10) VALUE(&NAME)" +
            (marker ? "\nCRTDTAARA DTAARA(QGPL/UNREACHED) TYPE(*CHAR) LEN(10) VALUE(&NAME)" : ""), null);
        var result = commands.Execute((rpg ? "CRTBNDRPG" : "CRTCLPGM") + " PGM(QGPL/DISPLAY) SRCFILE(QGPL/SOURCE)"); Assert.False(result.IsError, result.Message);
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
