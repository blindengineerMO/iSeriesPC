using Ipc.Services;
using Microsoft.Data.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Core.Tests.Services;

public sealed class HostLifecycleTests
{
    [Fact]
    public void Restart_preserves_changed_credentials_disabled_accounts_and_profile_configuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = IpcSystem.Create(directory))
            {
                first.Start();
                first.Security.ChangePassword("QSECOFR", File.ReadAllText(Path.Combine(directory, "system.db.initial-password")).Trim(), "CHANGED12");
                var user = first.Security.Profiles.Get("QUSER");
                user.Status = Ipc.Core.Security.ProfileStatus.Disabled;
                user.InitialCurrentLibrary = "WORK";
                first.Security.Profiles.Update(user);
            }
            using var second = IpcSystem.Create(directory);
            second.Start();
            Assert.True(second.Security.Profiles.VerifyPassword("QSECOFR", "CHANGED12"));
            Assert.False(second.Security.Profiles.VerifyPassword("QSECOFR", "11111111"));
            Assert.Equal(Ipc.Core.Security.ProfileStatus.Enabled, second.Security.Profiles.Get("QSECOFR").Status);
            Assert.Equal(Ipc.Core.Security.ProfileStatus.Disabled, second.Security.Profiles.Get("QUSER").Status);
            Assert.Equal("WORK", second.Security.Profiles.Get("QUSER").InitialCurrentLibrary);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Job_numbers_are_atomic_and_not_reused_after_deletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-jobids-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var system = IpcSystem.Create(directory);
            system.Start();
            var jobs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
                system.Jobs.CreateInteractive("TESTER"))));
            Assert.Equal(12, jobs.Select(j => j.Key.Number).Distinct().Count());
            var greatest = jobs.Max(j => j.Key.Number);
            using (var connection = system.Connections.Open())
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM sys_jobs";
                cmd.ExecuteNonQuery();
            }
            var next = system.Jobs.CreateInteractive("TESTER");
            Assert.True(next.Key.Number > greatest);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Start_is_idempotent_and_dispose_releases_owned_connections()
    {
        using var system = IpcSystem.Create(":memory:");
        Assert.False(system.IsReady);
        system.Start();
        system.Start();
        Assert.True(system.IsReady);
        Assert.Single(system.Log.Recent(10));
        system.Dispose();
        system.Dispose();
        Assert.False(system.IsReady);
        Assert.Throws<ObjectDisposedException>(() => system.Start());
        Assert.Throws<ObjectDisposedException>(() => system.Connections.Open());
    }

    [Fact]
    public void Failed_start_disposes_catalog_and_never_reports_ready()
    {
        using var system = IpcSystem.Create(":memory:");
        using (var connection = system.Connections.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE sys_meta(key TEXT, value TEXT); INSERT INTO sys_meta VALUES ('schema_version', '999')";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => system.Start());
        Assert.False(system.IsReady);
        Assert.Throws<ObjectDisposedException>(() => system.Connections.Open());
    }

    [Fact]
    public void Disposing_one_in_memory_system_does_not_close_another()
    {
        using var first = IpcSystem.Create(":memory:");
        using var second = IpcSystem.Create(":memory:");
        first.Start();
        second.Start();
        first.Dispose();
        Assert.True(second.IsReady);
        Assert.True(second.Libraries.LibraryExists("QSYS"));
    }
}
