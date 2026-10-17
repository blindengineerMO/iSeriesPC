using Ipc.Core.Catalog;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed class UserSpacePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-user-spaces-" + Guid.NewGuid().ToString("N"));
    private readonly QualifiedName _name = new("QGPL", "SPACE");
    [Fact]
    public void Version_24_upgrade_preserves_existing_queue_and_records_history_once()
    {
        using var factory = new SqliteConnectionFactory(_directory);
        new Migrator(factory, Migrator.Migrations.Take(24).ToArray()).MigrateToLatest();
        // Seed only the old schema, with real pre-upgrade queue state.
        var objects = new SqliteObjectStore(factory);
        foreach (var library in LibraryNames.SystemDefaults) objects.Create(new ObjectDescriptor { Key = new("QSYS", library.Name), ObjectType = ObjectType.Library });
        var queues = new DataQueueStore(factory); queues.Create(new("QGPL", "QUEUE"), 4);
        queues.Send(new("QGPL", "QUEUE"), new ProgramBuffer(new byte[] { 255, 0, 254, 1 }, 37));
        var migrator = new Migrator(factory); migrator.MigrateToLatest(); var backup = migrator.LastBackupPath;
        Assert.Equal(SystemCatalog.SchemaVersion, migrator.CurrentVersion()); Assert.True(File.Exists(backup));
        migrator.MigrateToLatest(); Assert.Equal(SystemCatalog.SchemaVersion, migrator.CurrentVersion());
        Assert.Equal("FF00FE01", Convert.ToHexString(queues.Receive(new("QGPL", "QUEUE"))!.Data.ToArray()));
        new UserSpaceStore(factory).Create(_name, 1, 0xFE);
        Assert.Equal(0xFE, new UserSpaceStore(factory).Read(_name, 1, 1)[0]);
    }

    [Fact]
    public async Task Ordinary_catalog_reopens_preserve_spaces_and_archives_but_server_restart_purges_replacements()
    {
        string identity;
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var spaces = new UserSpaceStore(system.Connections);
            spaces.Create(_name, 8, 0xFE); spaces.Create(_name, 8, 0xAA, replace: true);
            spaces.Write(_name, 3, [255, 0, 254, 1]); identity = spaces.Inspect(_name).Identity;
        }
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); Assert.Single(system.Objects.Find("QRPLOBJ", null, ObjectType.UserSpace, null));
            var spaces = new UserSpaceStore(system.Connections); Assert.Equal(identity, spaces.Inspect(_name).Identity);
            Assert.Equal("AAAAFF00FE01AAAA", Convert.ToHexString(spaces.Read(_name, 1, 8)));
            // Unrelated user-created spaces in QRPLOBJ are outside replacement recovery.
            spaces.Create(new("QRPLOBJ", "KEEPME"), 1, 0xCC);
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        try
        {
            await server.Ready.WaitAsync(stop.Token);
            using var factory = new SqliteConnectionFactory(_directory); var spaces = new UserSpaceStore(factory);
            Assert.Equal("KEEPME", Assert.Single(new SqliteObjectStore(factory).Find("QRPLOBJ", null, ObjectType.UserSpace, null)).Name);
            Assert.Equal("AAAAFF00FE01AAAA", Convert.ToHexString(spaces.Read(_name, 1, 8)));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Busy_writer_cancellation_leaves_existing_payload_and_identity_unchanged()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var spaces = new UserSpaceStore(system.Connections);
        spaces.Create(_name, 8, 0xFE); var before = spaces.Inspect(_name);
        using var blocker = system.Connections.Open(); using var transaction = blocker.BeginTransaction(deferred: false);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var writer = Task.Run(() => spaces.Write(_name, 1, new byte[] { 0 }, stop.Token));
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { stop.Cancel(); transaction.Rollback(); }
        Assert.Equal(before, spaces.Inspect(_name)); Assert.All(spaces.Read(_name, 1, 8), b => Assert.Equal(0xFE, b));
        spaces.Write(_name, 1, [0]); Assert.Equal(0, spaces.Read(_name, 1, 1)[0]);
    }

    [Fact]
    public async Task Busy_generic_delete_cancels_without_deleting_and_committed_deletions_survive_reopen()
    {
        using (var system = IpcSystem.Create(_directory))
        {
            system.Start(); var spaces = new UserSpaceStore(system.Connections);
            spaces.Create(new("QGPL", "MATCHA"), 2, 0xFE); spaces.Create(new("QGPL", "MATCHB"), 2, 0xAB);
            using var blocker = system.Connections.Open(); using var transaction = blocker.BeginTransaction(deferred: false);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var deleting = Task.Run(() => spaces.Delete("MATCH*", "QGPL", _ => ["QGPL"], stop.Token));
            try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deleting.WaitAsync(TimeSpan.FromSeconds(5))); }
            finally { stop.Cancel(); transaction.Rollback(); }
            Assert.Equal("FEFE", Convert.ToHexString(spaces.Read(new("QGPL", "MATCHA"), 1, 2)));
            Assert.Equal("ABAB", Convert.ToHexString(spaces.Read(new("QGPL", "MATCHB"), 1, 2)));
            system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DEPENDENT"), ObjectType = ObjectType.Program });
            new ObjectCatalogOperations(system.Connections).AddDependency(new("QGPL", "DEPENDENT"), ObjectType.Program, new("QGPL", "MATCHB"), ObjectType.UserSpace);
            Assert.Equal("CPF2117", Assert.Throws<CpfException>(() => spaces.Delete("MATCH*", "QGPL", _ => ["QGPL"])).MessageId);
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        Assert.False(reopened.Objects.Exists("QGPL", "MATCHA", ObjectType.UserSpace));
        Assert.Equal("ABAB", Convert.ToHexString(new UserSpaceStore(reopened.Connections).Read(new("QGPL", "MATCHB"), 1, 2)));
    }

    [Fact]
    public void Disallowed_user_domain_archive_is_destroyed_after_authorities_are_copied()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var spaces = new UserSpaceStore(system.Connections);
        spaces.Create(_name, 4, domain: "*USER", authority: "*EXCLUDE");
        system.Security.Authority.Grant("QGPL", "SPACE", ObjectType.UserSpace, "QUSER", Authorities.UseBits);
        system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QGPL");
        spaces.Create(_name, 2, replace: true);
        Assert.Empty(system.Objects.Find("QRPLOBJ", null, ObjectType.UserSpace, null));
        using (Ipc.Services.Events.OperationIdentity.Enter("QUSER")) Assert.Equal(new byte[2], spaces.Read(_name, 1, 2));
    }

    [Fact]
    public void Failed_archive_deletion_rolls_back_new_object_bytes_and_all_authorities()
    {
        using var system = IpcSystem.Create(_directory); system.Start(); var spaces = new UserSpaceStore(system.Connections);
        spaces.Create(_name, 4, 0xFE); var before = spaces.Inspect(_name);
        system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "DEPENDENT"), ObjectType = ObjectType.Program });
        new ObjectCatalogOperations(system.Connections).AddDependency(new("QGPL", "DEPENDENT"), ObjectType.Program, _name, ObjectType.UserSpace);
        system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QGPL");
        Assert.Equal("IPC0201", Assert.Throws<CpfException>(() => spaces.Create(_name, 2, replace: true)).MessageId);
        Assert.Equal(before, spaces.Inspect(_name)); Assert.All(spaces.Read(_name, 1, 4), b => Assert.Equal(0xFE, b));
        Assert.Empty(system.Objects.Find("QRPLOBJ", null, ObjectType.UserSpace, null));
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
