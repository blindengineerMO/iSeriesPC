using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Services.Configuration;
using Ipc.Services.Events;
using Ipc.Services.Logging;
using Ipc.Services.Menu;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Services;

public sealed class IpcSystem : IDisposable
{
    private readonly SystemConfig _config;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SystemValueRegistry _systemValueRegistry;
    private readonly EventBus _eventBus;
    private readonly HostLog _hostLog;
    private readonly SqliteObjectStore _objectStore;
    private readonly LibraryManager _libraryManager;
    private readonly Migrator _migrator;
    private readonly SecurityService _security;
    private readonly JobService _jobs;
    private readonly SubsystemService _subsystems;
    private readonly MenuStore _menus;

    private IpcSystem(
        SystemConfig config,
        SqliteConnectionFactory connectionFactory,
        SystemValueRegistry registry,
        EventBus bus,
        HostLog log,
        SqliteObjectStore store,
        LibraryManager libraryManager,
        Migrator migrator,
        SecurityService security,
        JobService jobs,
        SubsystemService subsystems,
        MenuStore menus)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _systemValueRegistry = registry;
        _eventBus = bus;
        _hostLog = log;
        _objectStore = store;
        _libraryManager = libraryManager;
        _migrator = migrator;
        _security = security;
        _jobs = jobs;
        _subsystems = subsystems;
        _menus = menus;
    }

    public static IpcSystem Create(string dataDirectory) =>
        Create(dataDirectory, null);

    public static IpcSystem Create(string dataDirectory, string? databaseFile)
    {
        var configStore = new ConfigStore(dataDirectory);
        var config = configStore.Load();
        var factory = new SqliteConnectionFactory(dataDirectory, databaseFile ?? "system.db");
        var migrator = new Migrator(factory);
        var registry = new SystemValueRegistry();
        var bus = new EventBus();
        var log = new HostLog(factory, Path.Combine(dataDirectory, "logs"));
        var store = new SqliteObjectStore(factory);
        var libraryManager = new LibraryManager(store, config.LibrarySuffix);
        var policy = new PasswordPolicy(registry);
        var profiles = new UserProfileStore(factory, policy);
        var authority = new AuthorityEngine(factory, store);
        var security = new SecurityService(profiles, policy, authority, registry);
        var jobs = new JobService(factory);
        var subsystems = new SubsystemService(factory, jobs);
        var menus = new MenuStore(store, factory);
        return new IpcSystem(config, factory, registry, bus, log, store, libraryManager, migrator,
            security, jobs, subsystems, menus);
    }

    public SystemConfig Config => _config;

    public SqliteConnectionFactory Connections => _connectionFactory;

    public IObjectStore Objects => _objectStore;

    public SystemValueRegistry SystemValues => _systemValueRegistry;

    public IEventBus Events => _eventBus;

    public HostLog Log => _hostLog;

    public LibraryManager Libraries => _libraryManager;

    public SecurityService Security => _security;

    public JobService Jobs => _jobs;

    public SubsystemService Subsystems => _subsystems;

    public MenuStore Menus => _menus;

    public void Start()
    {
        _migrator.MigrateToLatest();
        var valueStore = new SystemValueStore(_connectionFactory);
        valueStore.SeedFromRegistry(_systemValueRegistry);
        valueStore.LoadInto(_systemValueRegistry);
        _libraryManager.SeedSystemLibraries();
        _security.Profiles.SeedDefaults();
        _subsystems.SeedDefaults();
        _menus.SeedDefaults();
        _subsystems.Start(JobKeys.InteractiveSubsystem);
        _subsystems.Start(JobKeys.BatchSubsystem);
        _hostLog.Info($"iSeriesPC system '{_config.SystemName}' started");
        _eventBus.Publish(new SystemStartedEvent(_config.SystemName));
    }

    public void Dispose()
    {
    }
}