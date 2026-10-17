using System.Diagnostics;
using Ipc.Core.Catalog;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class JobTemporaryLibraryPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-qtemp-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = new();
    private static void Enable(IpcSystem system) => system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "PrivateJobFixture26");
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private static string Backing(IpcSystem system, Job job)
    {
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT library FROM sys_job_temp_libraries WHERE job_number=$job"; query.Parameters.AddWithValue("$job", job.Key.Number);
        return (string)query.ExecuteScalar()!;
    }
    [Fact]
    public async Task Independent_processes_share_their_job_namespace_without_cross_job_or_lost_writes()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system);
        var jobs = new[] { system.Jobs.CreateInteractive("QUSER"), system.Jobs.CreateInteractive("QUSER") };
        var spaces = new UserSpaceStore(system.Connections);
        foreach (var job in jobs) using (Enter(job)) spaces.Create(new("QTEMP", "PROCESS"), 32);
        var workers = new[] { Start(jobs[0], 0, 64), Start(jobs[0], 16, 65), Start(jobs[1], 0, 66), Start(jobs[1], 16, 67) };
        var errors = workers.Select(p => p.StandardError.ReadToEndAsync()).ToArray();
        var outputs = workers.Select(p => p.StandardOutput.ReadToEndAsync()).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (workers.Any(p => !File.Exists(Path.Combine(_directory, "ready-" + p.Id)))) await Task.Delay(10, stop.Token);
            File.WriteAllText(Path.Combine(_directory, "start"), "start");
            await Task.WhenAll(workers.Select(p => p.WaitForExitAsync(stop.Token)));
            for (var i = 0; i < workers.Length; i++) { Assert.True(workers[i].ExitCode == 0, await errors[i]); Assert.NotEmpty(await outputs[i]); }
            for (var i = 0; i < jobs.Length; i++) using (Enter(jobs[i]))
                Assert.Equal(Enumerable.Repeat((byte)(64 + i * 2), 16).Concat(Enumerable.Repeat((byte)(65 + i * 2), 16)), spaces.Read(new("QTEMP", "PROCESS"), 1, 32));
        }
        finally { stop.Cancel(); foreach (var worker in workers) if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } }
    }
    private Process Start(Job job, int offset, int count)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { typeof(WorkFixture.Program).Assembly.Location, "temp-space-write", _directory, count.ToString(), job.Key.Number.ToString(), job.Key.Name, job.Key.User, offset.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start)!; _children.Add(process); return process;
    }

    [Fact]
    public async Task Catalog_reopen_preserves_active_QTEMP_and_exclusive_server_recovery_cleans_interrupted_and_ended_jobs()
    {
        Job active, running, queued, ended; string[] gone;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); Enable(system); active = system.Jobs.CreateInteractive("QUSER");
            system.Jobs.Submit("RUNNING", "", "QBATCH", profile: "QUSER", command: "DSPJOB");
            running = new BatchQueue(system.Connections, system.Jobs).ClaimNext()!.Value.Job;
            queued = system.Jobs.Submit("QUEUED", "", "QBATCH", profile: "QUSER");
            ended = system.Jobs.CreateInteractive("QUSER");
            foreach (var job in new[] { active, running, ended }) using (Enter(job)) new UserSpaceStore(system.Connections).Create(new("QTEMP", "SPACE"), 1, 255);
            gone = new[] { Backing(system, active), Backing(system, running), Backing(system, ended) };
            using var connection = system.Connections.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sys_jobs SET execution_state='Interrupted',status='Completed' WHERE number=$job"; command.Parameters.AddWithValue("$job", ended.Key.Number); command.ExecuteNonQuery();
        }
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); using (Enter(active)) Assert.Equal(255, new UserSpaceStore(system.Connections).Read(new("QTEMP", "SPACE"), 1, 1)[0]);
            Assert.All(gone, library => Assert.True(system.Objects.Exists("QSYS", library, ObjectType.Library)));
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            using var factory = new SqliteConnectionFactory(_directory); var objects = new SqliteObjectStore(factory); var jobs = new JobService(factory);
            Assert.All(gone, library => { Assert.False(objects.Exists("QSYS", library, ObjectType.Library)); Assert.Empty(objects.Find(library, null, null, null)); });
            Assert.Equal(JobExecutionState.Interrupted, jobs.GetRequired(active.Key).ExecutionState);
            Assert.Equal(JobExecutionState.Interrupted, jobs.GetRequired(running.Key).ExecutionState);
            Assert.Equal(JobExecutionState.Queued, jobs.GetRequired(queued.Key).ExecutionState);
            using var connection = factory.Open(); using var query = connection.CreateCommand(); query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries";
            Assert.Equal(1L, query.ExecuteScalar());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void Queued_cancellation_and_failed_group_creation_do_not_leave_private_libraries()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system);
        var queued = system.Jobs.Submit("QUEUED", "", "QBATCH", profile: "QUSER"); var backing = Backing(system, queued);
        system.Jobs.RequestEnd(queued.Key); Assert.False(system.Objects.Exists("QSYS", backing, ObjectType.Library));
        var parent = system.Jobs.CreateInteractive("QUSER");
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "CREATE TRIGGER reject_group BEFORE UPDATE OF group_id ON sys_job_environment BEGIN SELECT RAISE(ABORT,'fixture'); END"; query.ExecuteNonQuery();
        var before = system.Objects.ObjectCount;
        using (Enter(parent)) Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => system.Jobs.CreateGroupJob(parent.Key, "REJECTED"));
        Assert.Equal(before, system.Objects.ObjectCount);
        query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries"; Assert.Equal(1L, query.ExecuteScalar());
        Assert.DoesNotContain(system.Jobs.List(), job => job.Key.Name == "REJECTED");
    }

    [Fact]
    public void A_failed_cleanup_rolls_back_job_completion_and_all_private_payload_deletions()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system);
        var job = system.Jobs.CreateInteractive("QUSER"); var backing = Backing(system, job);
        using (Enter(job))
        {
            new UserSpaceStore(system.Connections).Create(new("QTEMP", "SPACE"), 1, 255);
            new UserSpaceStore(system.Connections).Create(new("QTEMP", "ZZZ"), 2, 254);
        }
        using var connection = system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "CREATE TRIGGER reject_temp_delete BEFORE DELETE ON sys_user_spaces WHEN old.space_name='SPACE' BEGIN SELECT RAISE(ABORT,'fixture'); END"; query.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => system.Jobs.Complete(job.Key));
        Assert.Equal(JobExecutionState.Running, system.Jobs.GetRequired(job.Key).ExecutionState);
        Assert.Equal(backing, Backing(system, job));
        using (Enter(job))
        {
            Assert.Equal(255, new UserSpaceStore(system.Connections).Read(new("QTEMP", "SPACE"), 1, 1)[0]);
            Assert.Equal(new byte[] { 254, 254 }, new UserSpaceStore(system.Connections).Read(new("QTEMP", "ZZZ"), 1, 2));
        }
        query.CommandText = "DROP TRIGGER reject_temp_delete"; query.ExecuteNonQuery(); system.Jobs.Complete(job.Key);
        Assert.False(system.Objects.Exists("QSYS", backing, ObjectType.Library));
    }

    [Fact]
    public void Schema_25_upgrade_initializes_existing_live_jobs_once_without_rewriting_history()
    {
        using (var factory = new SqliteConnectionFactory(_directory))
        {
            new Migrator(factory, Migrator.Migrations.Take(25).ToArray()).MigrateToLatest();
            new LibraryManager(new SqliteObjectStore(factory)).SeedSystemLibraries();
            new Ipc.Services.Security.UserProfileStore(factory, new Ipc.Services.Security.PasswordPolicy(new Ipc.Core.System.SystemValueRegistry())).SeedDefaults();
            var objects = new SqliteObjectStore(factory);
            objects.Create(new ObjectDescriptor { Key = new("QUSRSYS", "QPRINT"), ObjectType = ObjectType.OutputQueue });
            objects.Create(new ObjectDescriptor { Key = new("QSYS", "QSYSOPR"), ObjectType = ObjectType.MessageQueue });
            using var connection = factory.Open(); using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO sys_jobs(number,name,user,type,status,user_profile,current_lib,libl,ccsid,submitted_at,execution_state) VALUES(101,'LEGACY','QUSER','Interactive','Active','QUSER','QGPL','QTEMP QGPL',37,'2026-10-17T00:00:00Z','Running')";
            insert.ExecuteNonQuery();
        }
        string identity;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); Enable(system); var job = system.Jobs.GetRequired(new(101, "LEGACY", "QUSER"));
            using (Enter(job))
            {
                var spaces = new UserSpaceStore(system.Connections); spaces.Create(new("QTEMP", "SPACE"), 1, 255); identity = spaces.Inspect(new("QTEMP", "SPACE")).Identity;
            }
            Assert.Equal(SystemCatalog.SchemaVersion, new Migrator(system.Connections).CurrentVersion());
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        using (Enter(reopened.Jobs.GetRequired(new(101, "LEGACY", "QUSER"))))
            Assert.Equal(identity, new UserSpaceStore(reopened.Connections).Inspect(new("QTEMP", "SPACE")).Identity);
        using var verify = reopened.Connections.Open(); using var query = verify.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries"; Assert.Equal(1L, query.ExecuteScalar());
        query.CommandText = "SELECT count(*) FROM sys_migrations"; Assert.Equal((long)SystemCatalog.SchemaVersion, query.ExecuteScalar());
    }
    [Fact]
    public async Task Two_authenticated_connections_have_independent_spaces_and_signoff_removes_only_the_owned_namespace()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); Enable(system);
            system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "READTEMP"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = """
                PGM
                DCL &DATA *CHAR LEN(1)
                CALL QSYS/QUSRTVUS PARM('SPACE     QTEMP     ' (1 (*INT 4)) (1 (*INT 4)) &DATA)
                IF (&DATA *EQ X'FE') THEN(SNDPGMMSG MSG('PRIVATE FE'))
                IF (&DATA *EQ X'AA') THEN(SNDPGMMSG MSG('PRIVATE AA'))
                ENDPGM
                """ });
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            await using var first = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "PrivateJobFixture26", stop.Token);
            await using var second = await CommandConnection.ConnectAsync(server.SocketPath, "QUSER", "PrivateJobFixture26", stop.Token);
            var created = await first.ExecuteAsync("CRTUSRSPC QTEMP/SPACE 1 VALUE(X'FE')", stop.Token); Assert.True(created.Success, created.Result?.Message);
            created = await second.ExecuteAsync("CRTUSRSPC QTEMP/SPACE 1 VALUE(X'AA')", stop.Token); Assert.True(created.Success, created.Result?.Message);
            var result = await first.ExecuteAsync("CALL QGPL/READTEMP", stop.Token); Assert.True(result.Success, result.Result?.Message); Assert.Contains("PRIVATE FE", result.Result?.Message);
            result = await second.ExecuteAsync("CALL QGPL/READTEMP", stop.Token); Assert.True(result.Success, result.Result?.Message); Assert.Contains("PRIVATE AA", result.Result?.Message);
            await first.SignoffAsync(stop.Token);
            result = await second.ExecuteAsync("CALL QGPL/READTEMP", stop.Token); Assert.True(result.Success, result.Result?.Message); Assert.Contains("PRIVATE AA", result.Result?.Message);
            using var factory = new SqliteConnectionFactory(_directory); using var connection = factory.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM sys_job_temp_libraries"; Assert.Equal(1L, query.ExecuteScalar());
            await second.SignoffAsync(stop.Token);
            Assert.Equal(0L, query.ExecuteScalar());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
