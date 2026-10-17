using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class TemporaryFilePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-private-files-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = new();
    private static void Enable(IpcSystem system) => system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QUSER"), "TemporaryFileProcess26");
    private static IDisposable Enter(Job job) => OperationIdentity.Enter(job.UserProfile ?? job.Key.User, job.Key);
    private static string Backing(IpcSystem system, Job job) { using var identity = Enter(job); return new JobTemporaryLibrary(system.Connections).ResolveLibrary("QTEMP"); }
    private static long Scalar(SqliteConnectionFactory factory, string sql, string library)
    {
        using var connection = factory.Open(); using var query = connection.CreateCommand(); query.CommandText = sql;
        query.Parameters.AddWithValue("$lib", library); return (long)query.ExecuteScalar()!;
    }
    [Fact]
    public async Task Concurrent_independent_processes_append_to_their_owned_members_without_cross_job_or_lost_rows()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system);
        var jobs = new[] { system.Jobs.CreateInteractive("QUSER"), system.Jobs.CreateInteractive("QUSER") };
        var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
        foreach (var job in jobs) using (Enter(job)) files.CreatePhysicalFile("QTEMP", "ROWS", TemporaryFileTests.Definition(), "");
        var workers = new[] { Start(jobs[0], 0), Start(jobs[0], 1000), Start(jobs[1], 2000), Start(jobs[1], 3000) };
        var outputs = workers.Select(p => p.StandardOutput.ReadToEndAsync()).ToArray(); var errors = workers.Select(p => p.StandardError.ReadToEndAsync()).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (workers.Any(p => !File.Exists(Path.Combine(_directory, "ready-" + p.Id)))) await Task.Delay(10, stop.Token);
            File.WriteAllText(Path.Combine(_directory, "start"), "start");
            await Task.WhenAll(workers.Select(p => p.WaitForExitAsync(stop.Token)));
            for (var i = 0; i < workers.Length; i++) { Assert.True(workers[i].ExitCode == 0, await errors[i]); Assert.NotEmpty(await outputs[i]); }
            for (var i = 0; i < jobs.Length; i++) using (Enter(jobs[i]))
            {
                var expected = Enumerable.Range(i * 2000, 32).Concat(Enumerable.Range(i * 2000 + 1000, 32)).Select(n => (long)n);
                Assert.Equal(expected, files.ReadKeyed("QTEMP", "ROWS", "ROWS").Select(row => (long)row["ID"]!));
            }
            var backing = Backing(system, jobs[0]); system.Jobs.Complete(jobs[0].Key);
            Assert.Equal(0, Scalar(system.Connections, "SELECT count(*) FROM sys_file_defs WHERE lib=$lib", backing));
            using (Enter(jobs[1])) Assert.Equal(64, files.RowCount("QTEMP", "ROWS", "ROWS"));
        }
        finally { stop.Cancel(); foreach (var worker in workers) if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } }
    }
    private Process Start(Job job, int offset)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { typeof(WorkFixture.Program).Assembly.Location, "temp-file-append", _directory, "32", job.Key.Number.ToString(), job.Key.Name, job.Key.User, offset.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start)!; _children.Add(process); return process;
    }
    [Fact]
    public async Task Catalog_reopen_preserves_private_sources_and_rows_until_exclusive_server_recovery_drops_them()
    {
        Job job; string backing;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); Enable(system); job = system.Jobs.CreateInteractive("QUSER"); backing = Backing(system, job);
            using (Enter(job))
            {
                var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
                files.CreateSourceFile("QTEMP", "SOURCE"); files.SaveSourceMember("QTEMP", "SOURCE", "MAIN", "PGM\nENDPGM", null);
                files.CreatePhysicalFile("QTEMP", "ROWS", TemporaryFileTests.Definition(unique: true), "");
                files.Insert("QTEMP", "ROWS", "ROWS", "ROW", new Dictionary<string, object?> { ["ID"] = 7, ["AMOUNT"] = 9007199254740992.01m, ["TEXT"] = "survived" });
            }
        }
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); using (Enter(job))
            {
                var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
                Assert.Equal("PGM\nENDPGM\n", files.ReadSourceMember("QTEMP", "SOURCE", "MAIN").Source);
                Assert.Equal(9007199254740992.01m, Assert.Single(files.ReadAll("QTEMP", "ROWS", "ROWS"))["AMOUNT"]);
            }
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            using var factory = new SqliteConnectionFactory(_directory);
            Assert.Equal(JobExecutionState.Interrupted, new JobService(factory).GetRequired(job.Key).ExecutionState);
            Assert.Equal(0, Scalar(factory, "SELECT count(*) FROM sys_file_defs WHERE lib=$lib", backing));
            Assert.Equal(0, Scalar(factory, "SELECT count(*) FROM sys_file_members WHERE lib=$lib", backing));
            Assert.Equal(0, Scalar(factory, "SELECT count(*) FROM sqlite_schema WHERE substr(tbl_name,1,11)=$lib||'.'", backing));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public void DDL_failure_rolls_back_private_descriptor_definition_and_member_metadata()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system); var job = system.Jobs.CreateInteractive("QUSER"); var backing = Backing(system, job);
        var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
        var bad = TemporaryFileTests.Definition(); bad.PrimaryFormat.Fields.Add(new() { Name = "ID", Type = Ipc.Db.Definitions.FieldType.Binary, Length = 4 });
        using (Enter(job))
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => files.CreatePhysicalFile("QTEMP", "ROWS", bad, ""));
            Assert.False(files.FileExists("QTEMP", "ROWS"));
        }
        Assert.Equal(0, Scalar(system.Connections, "SELECT count(*) FROM sys_file_defs WHERE lib=$lib", backing));
        Assert.Equal(0, Scalar(system.Connections, "SELECT count(*) FROM sys_file_members WHERE lib=$lib", backing));
        Assert.Equal(0, Scalar(system.Connections, "SELECT count(*) FROM sqlite_schema WHERE substr(tbl_name,1,11)=$lib||'.'", backing));
    }
    [Fact]
    public void Completion_failure_after_dropping_a_member_table_restores_schema_rows_and_job_state()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system); var job = system.Jobs.CreateInteractive("QUSER");
        var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", TemporaryFileTests.Definition(unique: true), "");
            files.Insert("QTEMP", "ROWS", "ROWS", "ROW", new Dictionary<string, object?> { ["ID"] = 9 });
        }
        using var connection = system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_private_definition_delete BEFORE DELETE ON sys_file_defs BEGIN SELECT RAISE(ABORT,'fixture'); END"; command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => system.Jobs.Complete(job.Key));
        Assert.Equal(JobExecutionState.Running, system.Jobs.GetRequired(job.Key).ExecutionState);
        using (Enter(job))
        {
            Assert.Equal(9L, Assert.Single(files.ReadAll("QTEMP", "ROWS", "ROWS"))["ID"]);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => files.Insert("QTEMP", "ROWS", "ROWS", "ROW", new Dictionary<string, object?> { ["ID"] = 9 }));
        }
        command.CommandText = "DROP TRIGGER fail_private_definition_delete"; command.ExecuteNonQuery(); system.Jobs.Complete(job.Key);
    }
    [Fact]
    public void Parsed_external_SQL_dependencies_prevent_cleanup_without_losing_private_rows()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); Enable(system); var job = system.Jobs.CreateInteractive("QUSER"); var backing = Backing(system, job);
        var files = new SqliteFileStore(system.Connections, system.Objects, "QUSER");
        using (Enter(job))
        {
            files.CreatePhysicalFile("QTEMP", "ROWS", TemporaryFileTests.Definition(), ""); files.Insert("QTEMP", "ROWS", "ROWS", "ROW", new Dictionary<string, object?> { ["ID"] = 1 });
        }
        using var connection = system.Connections.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIEW external_reference AS SELECT ID FROM \"" + backing + ".ROWS.ROWS\""; command.ExecuteNonQuery();
        Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => system.Jobs.Complete(job.Key)).MessageId);
        Assert.Equal(JobExecutionState.Running, system.Jobs.GetRequired(job.Key).ExecutionState);
        using (Enter(job)) Assert.Equal(1, files.RowCount("QTEMP", "ROWS", "ROWS"));
        command.CommandText = "DROP VIEW external_reference"; command.ExecuteNonQuery(); system.Jobs.Complete(job.Key);
    }
    public void Dispose()
    {
        foreach (var child in _children) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
