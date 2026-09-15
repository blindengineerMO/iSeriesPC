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
    private readonly object _lifecycleGate = new();
    private bool _started;
    private bool _disposed;
    private Timer? _maintenance;
    private readonly EimService _eim;
    private readonly CertificateService _certificates;
    private readonly ContentTrustService _contentTrust;
    private readonly ObjectSigningService _objectSigning;
    private readonly JobRuntimeManager _jobRuntime;
    private readonly CancellationTokenSource _identityStop = new();
    private Task? _identityRefresh;
    public string? MaintenanceError { get; private set; }

    public bool IsReady { get { lock (_lifecycleGate) return _started && !_disposed; } }

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
        MenuStore menus, EimService eim)
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
        _eim = eim;
        _certificates = new CertificateService(connectionFactory);
        _contentTrust = new ContentTrustService(connectionFactory, _certificates);
        _objectSigning = new ObjectSigningService(connectionFactory, _contentTrust);
        _jobRuntime = new JobRuntimeManager(connectionFactory);
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
        var log = new HostLog(factory, factory.UsesMemoryDatabase ? null : Path.Combine(dataDirectory, "logs"), config.Logging);
        var store = new SqliteObjectStore(factory);
        var libraryManager = new LibraryManager(store, config.LibrarySuffix);
        var policy = new PasswordPolicy(registry);
        var profiles = new UserProfileStore(factory, policy);
        var authority = new AuthorityEngine(factory, store);
        var eim = new EimService(factory, new LdapIdentityProbe(config.Authentication.Directories));
        var security = new SecurityService(profiles, policy, authority, registry, new DurableEventStore(factory), config.Authentication, eim: eim);
        var jobs = new JobService(factory);
        var subsystems = new SubsystemService(factory, jobs);
        var menus = new MenuStore(store, factory);
        return new IpcSystem(config, factory, registry, bus, log, store, libraryManager, migrator,
            security, jobs, subsystems, menus, eim);
    }

    public SystemConfig Config => global::System.Text.Json.JsonSerializer.Deserialize<SystemConfig>(
        global::System.Text.Json.JsonSerializer.Serialize(_config))!;

    public SqliteConnectionFactory Connections => _connectionFactory;

    public IObjectStore Objects => _objectStore;

    public ObjectCatalogOperations ObjectOperations => new(_connectionFactory);

    public SystemValueRegistry SystemValues
    {
        get
        {
            var snapshot = new SystemValueRegistry();
            foreach (var value in _systemValueRegistry.All) snapshot.Set(value.Name, value.Value);
            return snapshot;
        }
    }

    public IEventBus Events => _eventBus;

    public DurableEventStore DurableEvents => new(_connectionFactory);

    public HostLog Log => _hostLog;

    public LibraryManager Libraries => _libraryManager;

    public SecurityService Security => _security;
    public EimService EnterpriseIdentities => _eim;
    public CertificateService Certificates => _certificates;
    public ContentTrustService ContentTrust => _contentTrust;
    public ObjectSigningService ObjectSigning => _objectSigning;
    public SignedDeploymentService SignedDeployments => new(_connectionFactory, _contentTrust, _objectSigning);

    public JobService Jobs => _jobs;
    public ExternalProgramService ExternalPrograms => new(_connectionFactory, _objectSigning, _jobRuntime);

    public JobRuntimeManager JobRuntime => _jobRuntime;

    public WorkDefinitionStore WorkDefinitions => new(_connectionFactory);

    public SubsystemService Subsystems => _subsystems;

    public MenuStore Menus => _menus;

    public IEnumerable<string> SearchLibraries(Job? job, string library = "*LIBL")
    {
        library = library.ToUpperInvariant();
        var current = job?.CurrentLibrary is { } configured && ObjectName.IsValid(configured) ? configured : "QGPL";
        if (library == "*CURLIB") return new[] { current };
        if (library != "*LIBL") return new[] { library };
        var users = (job?.LibraryList is { } list && list != "*LIBL" ? list : "QGPL QUSRSYS")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var systemLibraries = SystemValues.Get(SystemValueNames.SystemLibraryList).Value
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return systemLibraries.Concat(job?.CurrentLibrary == "*CRTDFT" ? Array.Empty<string>() : new[] { current }).Concat(users).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public Ipc.Core.Menu.ApplicationMenu? FindMenu(string name, Job? job = null, string? library = null)
    {
        var slash = name.IndexOf('/');
        if (slash >= 0) { library = name[..slash]; name = name[(slash + 1)..]; }
        return SearchLibraries(job, library ?? "*LIBL")
            .Select(lib => Menus.TryGet(name.ToUpperInvariant(), lib)).FirstOrDefault(menu => menu is not null);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            try
            {
                StartCore();
                _started = true;
            }
            catch
            {
                _jobRuntime.Dispose();
            _security.Dispose();
                _certificates.Dispose();
                _connectionFactory.Dispose();
                _disposed = true;
                throw;
            }
        }
    }

    private void StartCore()
    {
        _migrator.MigrateToLatest();
        var valueStore = new SystemValueStore(_connectionFactory);
        _systemValueRegistry.Set(SystemValueNames.SystemName, _config.SystemName);
        _systemValueRegistry.Set(SystemValueNames.Ccsid, _config.Ccsid.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        valueStore.SeedFromRegistry(_systemValueRegistry);
        valueStore.LoadInto(_systemValueRegistry);
        _config.SystemName = _systemValueRegistry.Get(SystemValueNames.SystemName).Value;
        _config.Ccsid = int.Parse(_systemValueRegistry.Get(SystemValueNames.Ccsid).Value, global::System.Globalization.CultureInfo.InvariantCulture);
        _libraryManager.SeedSystemLibraries();
        _security.Profiles.SeedDefaults();
        _jobs.SeedQueues();
        WorkDefinitions.SeedClasses();
        _subsystems.SeedDefaults();
        _menus.SeedDefaults();
        new Ipc.Services.Commands.CommandDefinitionStore(_connectionFactory).SeedDefaults();
        _subsystems.Start(JobKeys.InteractiveSubsystem);
        _subsystems.Start(JobKeys.BatchSubsystem);
        _subsystems.Start(JobKeys.CommunicationSubsystem);
        _subsystems.Start(JobKeys.SystemSubsystem);
        WorkDefinitions.SeedJobDescriptions();
        _hostLog.Maintain(DateTimeOffset.UtcNow);
        DurableEvents.Append("system.started", new { _config.SystemName });
        _hostLog.Info($"iSeriesPC system '{_config.SystemName}' started");
        _eventBus.Publish(new SystemStartedEvent(_config.SystemName));
        _maintenance = new Timer(_ =>
        {
            lock (_lifecycleGate)
            {
                if (_disposed) return;
                try { _hostLog.Maintain(DateTimeOffset.UtcNow); MaintenanceError = null; }
                catch (Exception ex) { MaintenanceError = ex.Message; }
            }
        }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        if (_config.Authentication.Directories.Count > 0) _identityRefresh = Task.Run(async () =>
        {
            try
            {
                while (!_identityStop.IsCancellationRequested)
                {
                    try { _eim.RefreshAll(_identityStop.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { MaintenanceError = $"Identity refresh failed: {ex.GetType().Name}"; }
                    await Task.Delay(TimeSpan.FromSeconds(30), _identityStop.Token);
                }
            }
            catch (OperationCanceledException) when (_identityStop.IsCancellationRequested) { }
        });
    }

    public void SetSystemValue(string name, string value)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            name = name.ToUpperInvariant();
            if (!_systemValueRegistry.TryGet(name, out _)) throw new CpfException("IPC0003", "Unknown system value.");
            var writable = new[] { SystemValueNames.SystemName, SystemValueNames.Ccsid, SystemValueNames.SecurityLevel,
                SystemValueNames.PasswordMinimumLength, SystemValueNames.PasswordSystemLevel, SystemValueNames.PasswordExpirationInterval,
                SystemValueNames.MaximumSignOnAttempts, SystemValueNames.PasswordRequiredDigit,
                SystemValueNames.PasswordRepeatedCharacters, SystemValueNames.SystemLibraryList, SystemValueNames.UserLibraryList };
            if (!writable.Contains(name)) throw new CpfException("IPC0002", "Changing this system value is not implemented.");
            if (name == SystemValueNames.SystemName && !ObjectName.IsValid(value))
                throw new CpfException("IPC0003", "System name must be a valid object name.");
            if (name is SystemValueNames.PasswordRequiredDigit or SystemValueNames.PasswordRepeatedCharacters && value is not ("*YES" or "*NO"))
                throw new CpfException("IPC0003", "Value must be *YES or *NO.");
            if (name == SystemValueNames.PasswordSystemLevel && (value is not ("0" or "1" or "2" or "3" or "4") ||
                value is "0" or "1" && _security.PasswordPolicy.MinimumLength > 10))
                throw new CpfException("IPC0003", "QPWDLVL must be 0 through 4 and accommodate QPWDMINLEN.");
            if (name is SystemValueNames.SystemLibraryList or SystemValueNames.UserLibraryList)
            {
                var libraries = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (libraries.Length > 250 || libraries.Distinct().Count() != libraries.Length || libraries.Any(l => !_libraryManager.LibraryExists(l)))
                    throw new CpfException("IPC0003", "Library list must contain existing unique libraries.");
                value = string.Join(',', libraries);
            }
            if (_systemValueRegistry.Get(name).ValueType == SystemValueType.Numeric)
            {
                if (!int.TryParse(value, out var number) || number < 0 || number > 65535)
                    throw new CpfException("IPC0003", "Invalid numeric system value.");
                if (name == SystemValueNames.SecurityLevel && number is not (10 or 20 or 30 or 40 or 50))
                    throw new CpfException("IPC0003", "QSECURITY must be 10, 20, 30, 40 or 50.");
                if (name == SystemValueNames.Ccsid && !Ipc.Core.Text.CodePage.IsSupported(number))
                    throw new CpfException("IPC0003", "Unsupported CCSID.");
                if (name is SystemValueNames.PasswordMinimumLength or SystemValueNames.MaximumSignOnAttempts && number == 0)
                    throw new CpfException("IPC0003", "Value must be positive.");
                if (name == SystemValueNames.PasswordMinimumLength && number > _security.PasswordPolicy.MaximumLength)
                    throw new CpfException("IPC0003", "Minimum password length exceeds QPWDLVL maximum.");
            }
            new SystemValueStore(_connectionFactory).Set(name, value);
            _systemValueRegistry.Set(name, value);
            if (name == SystemValueNames.SystemName) _config.SystemName = value;
            if (name == SystemValueNames.Ccsid) _config.Ccsid = int.Parse(value, global::System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _maintenance?.Dispose();
            _identityStop.Cancel();
            // Native directory calls have finite transport timeouts. A cancelled
            // probe checks cancellation before touching the catalog again.
            try { _identityRefresh?.Wait(TimeSpan.FromSeconds(12)); }
            catch (AggregateException) { }
            _jobRuntime.Dispose();
            _security.Dispose();
            _certificates.Dispose();
            _connectionFactory.Dispose();
            _identityStop.Dispose();
        }
    }
}
