using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public sealed class EimService(SqliteConnectionFactory factory, IDirectoryIdentityProbe directory)
{
    private readonly EimMappingStore _store = new(factory);
    public EimMappingStore Mappings => _store;

    public DirectoryIdentityState Refresh(string profile)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        var mapping = _store.GetInternal(profile) ?? throw new CpfException("IPC0003", "EIM mapping not found.");
        return CheckCurrent(mapping);
    }

    internal DirectoryIdentityState CheckCurrent(EimMapping mapping, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var state = directory.Check(mapping);
        cancellationToken.ThrowIfCancellationRequested();
        // An old probe can never update a removed/replaced mapping.
        return _store.RecordProbe(mapping, state, started) ? state : DirectoryIdentityState.Unknown;
    }

    internal void RefreshAll(CancellationToken cancellationToken = default)
    {
        foreach (var mapping in _store.List()) CheckCurrent(mapping, cancellationToken);
    }
}
