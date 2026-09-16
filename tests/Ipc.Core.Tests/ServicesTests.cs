using Ipc.Core.Catalog;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Logging;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests.Services;

public class SqliteInfrastructureTests
{
    [Fact]
    public void Migrator_creates_schema_and_versions()
    {
        var factory = new SqliteConnectionFactory($":memory:");
        var migrator = new Migrator(factory);
        Assert.Equal(0, migrator.CurrentVersion());
        migrator.MigrateToLatest();
        Assert.Equal(SystemCatalog.SchemaVersion, migrator.CurrentVersion());
    }

    [Fact]
    public void Sqlite_object_store_crud()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        var store = new SqliteObjectStore(factory);

        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("APPLIB", "CUSTMAST"),
            ObjectType = ObjectType.File,
            Attribute = "*PF",
            Description = "Customer master",
            Ccsid = 37,
        });

        var loaded = store.GetRequired("APPLIB", "CUSTMAST", ObjectType.File);
        Assert.Equal("*PF", loaded.Attribute);
        Assert.Equal("Customer master", loaded.Description);

        store.Update(loaded);
        Assert.NotNull(store.Get("APPLIB", "CUSTMAST", ObjectType.File));

        store.Delete("APPLIB", "CUSTMAST", ObjectType.File);
        Assert.False(store.Exists("APPLIB", "CUSTMAST", ObjectType.File));
        Assert.Throws<CpfException>(() => store.GetRequired("APPLIB", "CUSTMAST", ObjectType.File));
    }

    [Fact]
    public void Sqlite_object_store_find_with_generics()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        var store = new SqliteObjectStore(factory);
        Seed(store, "QSYS", "MYLIB", ObjectType.Library, "*PROD");
        Seed(store, "QSYS", "OTHER", ObjectType.Library, "*PROD");
        Seed(store, "MYLIB", "CUSTMAST", ObjectType.File, "*PF");
        Seed(store, "MYLIB", "CUSTMRGE", ObjectType.File, "*PF");
        Seed(store, "MYLIB", "ORDERS", ObjectType.File, "*PF");
        Seed(store, "OTHER", "CUSTMAST", ObjectType.File, "*PF");

        var matches = store.Find("MYLIB", "CUST*", null, null);
        Assert.Equal(new[] { "CUSTMAST", "CUSTMRGE" }, matches.Select(m => m.Name).ToArray());

        var justFiles = store.Find("MYLIB", null, ObjectType.File, null);
        Assert.Equal(3, justFiles.Count);

        var list = store.ListLibraries();
        Assert.Equal(new[] { "MYLIB", "OTHER" }, list);
    }

    [Fact]
    public void Sqlite_object_store_round_trips_extended_attributes()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        var store = new SqliteObjectStore(factory);

        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("LIB", "OBJ"),
            ObjectType = ObjectType.Program,
            ExtendedAttributes = new Dictionary<string, string> { ["Info"] = "x", ["Src"] = "abcd" },
        });

        var loaded = store.GetRequired("LIB", "OBJ", ObjectType.Program);
        Assert.Equal("x", loaded.ExtendedAttributes!["Info"]);
        Assert.Equal("abcd", loaded.ExtendedAttributes["Src"]);
    }

    private static void Seed(Ipc.Core.Objects.IObjectStore store, string lib, string name, string type, string attr) =>
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName(lib, name),
            ObjectType = type,
            Attribute = attr,
        });
}

public class HostLogTests
{
    [Fact]
    public void Log_messages_are_queryable()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();
        var log = new HostLog(factory);
        log.Info("boot");
        log.Info("loaded");
        var entries = log.Recent(10);
        Assert.Equal(2, entries.Count);
        Assert.Equal("boot", entries[0].Message);
        Assert.Equal("loaded", entries[1].Message);
    }
}

public class SystemValueStoreTests
{
    [Fact]
    public void Values_persist_and_reload()
    {
        var factory = new SqliteConnectionFactory(":memory:");
        new Migrator(factory).MigrateToLatest();

        var registry1 = new SystemValueRegistry();
        registry1.Set(SystemValueNames.SystemName, "PERSISTED");
        new SystemValueStore(factory).SeedFromRegistry(registry1);

        var registry2 = new SystemValueRegistry();
        new SystemValueStore(factory).LoadInto(registry2);
        Assert.Equal("PERSISTED", registry2.Get(SystemValueNames.SystemName).Value);
    }
}

public class EventBusTests
{
    [Fact]
    public void Subscribers_receive_events()
    {
        var bus = new EventBus();
        var seen = new List<ObjectCreatedEvent>();
        bus.Subscribe<ObjectCreatedEvent>(e => seen.Add(e));

        bus.Publish(new ObjectCreatedEvent("LIB", "OBJ", "*PGM"));
        bus.Publish(new ObjectCreatedEvent("LIB2", "OBJ2", "*PGM"));

        Assert.Equal(2, seen.Count);
        Assert.Equal("OBJ", seen[0].Name);
    }

    [Fact]
    public void Unsubscribed_event_types_are_ignored()
    {
        var bus = new EventBus();
        var count = 0;
        bus.Subscribe<ObjectCreatedEvent>(_ => count++);
        bus.Publish(new SystemStartedEvent("SYS"));
        Assert.Equal(0, count);
    }
}

public class ServiceRegistryTests
{
    [Fact]
    public void Resolves_singletons_and_factories()
    {
        var registry = new Ipc.Services.Container.ServiceRegistry();
        registry.RegisterSingleton(new Ipc.Core.System.SystemValueRegistry());
        registry.Register<IEventBus>(_ => new EventBus());

        Assert.Same(
            registry.Resolve<SystemValueRegistry>(),
            registry.Resolve<SystemValueRegistry>());
        Assert.NotSame(registry.Resolve<IEventBus>(), registry.Resolve<IEventBus>());
        Assert.Throws<InvalidOperationException>(() => registry.Resolve<HostLog>());
    }
}

public class IpcSystemTests
{
    [Fact]
    public void Start_seeds_libraries_and_values()
    {
        var system = IpcSystem.Create(":memory:");
        system.Start();

        Assert.True(system.Libraries.LibraryExists("QGPL"));
        Assert.Equal("40", system.SystemValues.Get(SystemValueNames.SecurityLevel).Value);
        Assert.Equal(LibraryNames.SystemDefaults.Count + 14 + Ipc.Core.Menu.SystemMenus.All().Count + Ipc.Services.Commands.CommandDefinitionStore.BuiltinNames.Count + system.Security.Profiles.ListAll().Count, system.Objects.ObjectCount);
        Assert.Equal("IPCAPI", system.Objects.GetRequired("QSYS", "QCMDEXC", ObjectType.Program).Attribute);
        system.Log.Info("integration ok");
        Assert.Equal(2, system.Log.Recent(10).Count);
    }
}