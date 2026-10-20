using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

public sealed partial class ServiceAuthorization
{
    // Recheck view writes on the publisher connection, including changes made by
    // transactional hooks. Delegation never replaces the authenticated caller.
    internal void RequireBoundObject(SqliteConnection connection, SqliteTransaction transaction, string library, string name,
        AuthorityBit required, string? viewOwner = null, AuthorityBit alternatives = AuthorityBit.None, string objectType = ObjectType.File, bool checkLibrary = true)
    {
        var caller = OperationIdentity.Current;
        if (caller is not null) RequireCaller(connection, transaction, caller.Principal);
        if (caller is null && viewOwner is null) { if (objectType == ObjectType.File) SqlConstraintState.RequireAccess(connection, transaction, library, name, required); return; }
        var principal = viewOwner ?? caller!.Principal;
        var profile = UserProfileStore.ReadForAuthentication(connection, transaction, principal);
        if (profile is null) throw new CpfException("CPF9802", "View authority profile is unavailable.");
        var groups = UserProfileStore.ReadEffectiveGroups(connection, transaction, principal);
        var engine = new AuthorityEngine(factory, new SqliteObjectStore(factory));
        var container = checkLibrary ? ObjectSigningService.Read(connection, transaction, "QSYS", library, ObjectType.Library) : null;
        var descriptor = ObjectSigningService.Read(connection, transaction, library, name, objectType);
        if (checkLibrary && !Has(Effective(container!), AuthorityBit.ObjectOperate) || !Has(Effective(descriptor), required) || alternatives != AuthorityBit.None && (Effective(descriptor) & alternatives) == AuthorityBit.None)
            throw new CpfException("CPF9802", "View operation is not authorized.");
        if (objectType == ObjectType.File) SqlConstraintState.RequireAccess(connection, transaction, library, name, required);
        AuthorityBit Effective(ObjectDescriptor item)
        {
            var bits = engine.ComputeEffective(connection, transaction, profile, groups, item);
            if (viewOwner is null && caller is not null) foreach (var owner in caller.AdoptedOwners().Distinct(StringComparer.Ordinal))
                bits |= engine.AdoptedAuthority(connection, transaction, owner, item);
            return bits;
        }
    }
}
