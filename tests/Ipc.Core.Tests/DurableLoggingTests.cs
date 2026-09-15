using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Logging;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class DurableLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-events-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Unacknowledged_events_replay_after_restart_and_cursors_are_independent()
    {
        long sequence;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start();
            var initial = system.DurableEvents.Read("worker", 1000);
            system.DurableEvents.Acknowledge(initial);
            sequence = system.DurableEvents.Append("test.work", new { value = 42 });
            Assert.Equal(sequence, Assert.Single(system.DurableEvents.Read("worker").Events).Sequence);
        }
        using var restarted = IpcSystem.Create(_directory);
        restarted.Start();
        var batch = restarted.DurableEvents.Read("worker", 1);
        Assert.Equal(sequence, Assert.Single(batch.Events).Sequence);
        var duplicate = restarted.DurableEvents.Read("worker", 1);
        restarted.DurableEvents.Acknowledge(batch);
        Assert.Throws<InvalidOperationException>(() => restarted.DurableEvents.Acknowledge(duplicate));
        Assert.DoesNotContain(restarted.DurableEvents.Read("worker").Events, e => e.Sequence == sequence);
        Assert.Contains(restarted.DurableEvents.Read("independent", 1000).Events, e => e.Sequence == sequence);
    }

    [Fact]
    public async Task Concurrent_identities_are_isolated_and_rolled_back_mutations_emit_no_events()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        for (var index = 1; index <= 8; index++)
        {
            system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = $"USER{index}" });
            system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, $"USER{index}", Authorities.ChangeBits);
        }
        await Task.WhenAll(Enumerable.Range(1, 8).Select(index => Task.Run(() =>
        {
            using var identity = OperationIdentity.Enter($"USER{index}", new JobKey(index, "JOB", $"USER{index}"));
            system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", $"OBJ{index}"), ObjectType = ObjectType.Program, Owner = $"USER{index}" });
            Assert.Equal($"USER{index}", OperationIdentity.Current!.Principal);
        })));
        Assert.Null(OperationIdentity.Current);
        var events = system.DurableEvents.Read("audit", 1000).Events.Where(e => e.Kind == "object.created" && e.Principal.StartsWith("USER", StringComparison.Ordinal) && e.Payload.Contains("OBJ")).ToArray();
        Assert.Equal(8, events.Length);
        foreach (var entry in events) Assert.Equal(entry.Principal[4..] + "/JOB/" + entry.Principal, entry.Job);
        using var connection = system.Connections.Open();
        using (var transaction = connection.BeginTransaction())
        {
            new SqliteObjectStore(system.Connections).Create(new ObjectDescriptor { Key = new("QGPL", "ROLLBACK"), ObjectType = ObjectType.Program }, connection, transaction);
            transaction.Rollback();
        }
        Assert.DoesNotContain(system.DurableEvents.Read("audit", 1000).Events, e => e.Payload.Contains("ROLLBACK"));
    }

    [Fact]
    public async Task Failed_delivery_replays_the_batch_without_advancing_acknowledgement()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        system.DurableEvents.Acknowledge(system.DurableEvents.Read("handler", 1000));
        var sequence = system.DurableEvents.Append("test.delivery", new { });
        await Assert.ThrowsAsync<IOException>(() => system.DurableEvents.DeliverAsync("handler", (_, _) => throw new IOException("Consumer unavailable")));
        Assert.Equal(sequence, Assert.Single(system.DurableEvents.Read("handler").Events).Sequence);
        var seen = new List<long>();
        Assert.Equal(1, await system.DurableEvents.DeliverAsync("handler", (item, _) => { seen.Add(item.Sequence); return Task.CompletedTask; }));
        Assert.Equal(new[] { sequence }, seen);
        Assert.Empty(system.DurableEvents.Read("handler").Events);
    }

    [Fact]
    public void Retention_preserves_unacknowledged_audit_and_active_job_logs()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        var job = system.Jobs.CreateInteractive("QUSER");
        system.DurableEvents.Read("slow", 1000);
        var audit = system.DurableEvents.Append("security.test", new { });
        system.DurableEvents.Append("test.old", new { });
        var future = DateTimeOffset.UtcNow.AddDays(40);
        system.DurableEvents.Retain(future);
        Assert.Contains(system.DurableEvents.Read("slow", 1000).Events, e => e.Sequence == audit);
        Assert.NotEmpty(system.Jobs.GetLog(job.Key));
        system.DurableEvents.Acknowledge(system.DurableEvents.Read("slow", 1000));
        system.DurableEvents.Retain(future);
        Assert.DoesNotContain(system.DurableEvents.Read("check", 1000).Events, e => e.Kind == "test.old");
        Assert.Contains(system.DurableEvents.Read("check", 1000).Events, e => e.Sequence == audit);
        system.DurableEvents.RemoveConsumer("check");
        system.Jobs.Complete(job.Key);
        system.DurableEvents.Acknowledge(system.DurableEvents.Read("slow", 1000));
        system.DurableEvents.Retain(future.AddDays(60));
        Assert.Empty(system.Jobs.GetLog(job.Key));
        Assert.Empty(system.DurableEvents.Read("slow", 1000).Events);
    }

    [Fact]
    public void Mirror_rotation_is_bounded_and_history_survives_mirror_failure()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        var mirrors = Path.Combine(_directory, "bounded");
        var log = new HostLog(system.Connections, mirrors, new(MaximumFileBytes: 1024, MaximumFiles: 3));
        for (var index = 0; index < 12; index++) log.Info(new string('x', 700) + "\nforged entry");
        var files = Directory.GetFiles(mirrors);
        Assert.Equal(3, files.Length);
        Assert.All(files, path =>
        {
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
            Assert.Single(File.ReadAllLines(path));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        });
        var impossible = Path.Combine(_directory, "file-not-directory");
        File.WriteAllText(impossible, "occupied");
        var degraded = new HostLog(system.Connections, impossible);
        degraded.Info("durable despite mirror failure");
        Assert.NotNull(degraded.LastMirrorError);
        Assert.Equal("durable despite mirror failure", degraded.Recent(1)[0].Message);
        system.Dispose();
        Assert.Throws<ObjectDisposedException>(() => degraded.Info("database unavailable"));
    }

    [Fact]
    public void Authentication_audit_records_attempt_result_without_credentials()
    {
        using var system = IpcSystem.Create(_directory);
        system.Start();
        Assert.False(system.Security.Authenticate("QUSER", "PRIVATE_PASSWORD").Success);
        var audit = Assert.Single(system.DurableEvents.Read("audit", 1000).Events, e => e.Kind == "security.authentication");
        Assert.Equal("*UNAUTHENTICATED", audit.Principal);
        Assert.Contains("QUSER", audit.Payload);
        Assert.DoesNotContain("PRIVATE_PASSWORD", audit.Payload);
        Assert.DoesNotContain("password_hash", string.Join("", system.DurableEvents.Read("audit", 1000).Events.Select(e => e.Payload)));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
