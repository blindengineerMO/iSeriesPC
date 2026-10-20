using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Checks server-established callers at service boundaries. No identity denotes trusted host code.</summary>
public sealed partial class ServiceAuthorization(SqliteConnectionFactory factory)
{
    public void RequireObject(string library, string name, string type, AuthorityBit required, bool checkLibrary = true, bool acquireLocks = true)
        => RequireObjectCore(library, name, type, required, AuthorityBit.None, checkLibrary, acquireLocks);

    public void RequireObjectAny(string library, string name, string type, AuthorityBit required, AuthorityBit alternatives,
        bool checkLibrary = true, bool acquireLocks = true)
    {
        if (alternatives == AuthorityBit.None) throw new ArgumentException("At least one alternative authority is required.", nameof(alternatives));
        RequireObjectCore(library, name, type, required, alternatives, checkLibrary, acquireLocks);
    }

    private void RequireObjectCore(string library, string name, string type, AuthorityBit required, AuthorityBit alternatives, bool checkLibrary, bool acquireLocks)
    {
        var key = new Ipc.Services.Work.JobTemporaryLibrary(factory).Resolve(new(library, name), type);
        library = key.Library; name = key.Name.Value;
        if (OperationIdentity.Current is not { } identity) return;
        var (profile, groups) = Caller(identity.Principal);
        var objects = new SqliteObjectStore(factory);
        var engine = new AuthorityEngine(factory, objects);
        if (checkLibrary && !(library == "QSYS" && type == ObjectType.Library))
        {
            var container = objects.GetForAuthorization("QSYS", library, ObjectType.Library);
            if (container is null || !Has(Effective(engine, profile, groups, container), AuthorityBit.ObjectOperate))
                Deny(library, name, type, required);
        }
        var descriptor = objects.GetForAuthorization(library, name, type);
        var granted = descriptor is null ? AuthorityBit.None : Effective(engine, profile, groups, descriptor);
        if (descriptor is null || !Has(granted, required) || alternatives != AuthorityBit.None && (granted & alternatives) == AuthorityBit.None)
            Deny(library, name, type, required);
        var choices = (int)(granted & alternatives);
        var selected = (AuthorityBit)(choices & -choices);
        if (acquireLocks)
        {
            var locks = new Ipc.Services.Work.JobLockStore(factory);
            if (checkLibrary && !(library == "QSYS" && type == ObjectType.Library))
                locks.RequireAccess("QSYS", library, ObjectType.Library, Authorities.UseBits);
            locks.RequireAccess(library, name, type, required | selected);
        }
    }

    // Data boundaries opt in after authorization. Object lookup and allocation
    // inspection use read authority without reading records, including during repair.
    internal void RequireFileDataState(string library, string name, AuthorityBit required)
    {
        if ((required & SqlConstraintState.Data) == 0) return;
        using var connection = factory.Open(); SqlConstraintState.RequireAccess(connection, null, library, name, required);
    }

    internal void RequireSnapshot(ObjectDescriptor descriptor, AuthorityBit required)
    {
        var key = new Ipc.Services.Work.JobTemporaryLibrary(factory).Resolve(descriptor.Key, descriptor.ObjectType);
        if (key != descriptor.Key) descriptor = descriptor.Snapshot(key);
        if (OperationIdentity.Current is not { } identity) return;
        var (profile, groups) = Caller(identity.Principal); var objects = new SqliteObjectStore(factory); var engine = new AuthorityEngine(factory, objects);
        var library = objects.GetForAuthorization("QSYS", descriptor.Library, ObjectType.Library);
        if (library is null || !Has(Effective(engine, profile, groups, library), AuthorityBit.ObjectOperate) || !Has(Effective(engine, profile, groups, descriptor), required))
            Deny(descriptor.Library, descriptor.Name, descriptor.ObjectType, required);
    }

    // View expansion delegates only its recorded owner's table rights. The reader's
    // authentication/job stays intact, and adopted program authority is not imported.
    internal void RequireViewSource(string owner, string library, string name, AuthorityBit required)
    {
        if (OperationIdentity.Current is { } caller) Caller(caller.Principal);
        using var connection = factory.Open();
        var profile = UserProfileStore.ReadForAuthentication(connection, null, owner);
        if (profile is null) Deny(library, name, ObjectType.File, required);
        var profiles = new UserProfileStore(factory, new PasswordPolicy(new SystemValueRegistry()));
        var objects = new SqliteObjectStore(factory); var engine = new AuthorityEngine(factory, objects);
        var container = objects.GetForAuthorization("QSYS", library, ObjectType.Library);
        var descriptor = objects.GetForAuthorization(library, name, ObjectType.File);
        var groups = profiles.ReadEffectiveGroups(owner);
        if (container is null || descriptor is null || !Has(engine.ComputeEffective(profile!, groups, container), AuthorityBit.ObjectOperate) ||
            !Has(engine.ComputeEffective(profile!, groups, descriptor), required)) Deny(library, name, ObjectType.File, required);
        SqlConstraintState.RequireAccess(connection, null, library, name, required);
    }

    private static AuthorityBit Effective(AuthorityEngine engine, UserProfile profile, IReadOnlyList<string> groups, ObjectDescriptor descriptor)
    {
        var bits = engine.ComputeEffective(profile, groups, descriptor);
        if (OperationIdentity.Current is { } identity)
            foreach (var owner in identity.AdoptedOwners().Distinct(StringComparer.Ordinal)) bits |= engine.AdoptedAuthority(owner, descriptor);
        return bits;
    }

    public void RequireObjectOwner(ObjectDescriptor descriptor)
    {
        if (OperationIdentity.Current is not { } identity) return;
        var (profile, groups) = Caller(identity.Principal);
        if (descriptor.Owner != profile.Name && !groups.Contains(descriptor.Owner, StringComparer.Ordinal)) RequireSpecial(SpecialAuthority.AllObject, allowAdopted: false);
    }

    public void RequireAdoption(ObjectDescriptor program)
    {
        if (OperationIdentity.Current is { } identity && program.AdoptsOwnerAuthority && program.Owner != identity.Principal)
            RequireObject("QSYS", program.Owner, ObjectType.UserProfile, Authorities.UseBits);
    }

    public void RequireCreate(ObjectDescriptor descriptor)
    {
        var key = new Ipc.Services.Work.JobTemporaryLibrary(factory).Resolve(descriptor.Key, descriptor.ObjectType);
        if (key != descriptor.Key) descriptor = descriptor.Snapshot(key);
        if (OperationIdentity.Current is not { } identity) return;
        if (descriptor.ObjectType == ObjectType.Program && descriptor.Attribute == Ipc.Services.Work.ExternalProgramService.Attribute)
            RequireSpecial(SpecialAuthority.Service, allowAdopted: false);
        if (descriptor.ObjectType is ObjectType.UserProfile or ObjectType.AuthorizationList)
            RequireSpecial(SpecialAuthority.SecurityAdministrator);
        else RequireObject("QSYS", descriptor.Library, ObjectType.Library, AuthorityBit.ObjectOperate | AuthorityBit.Add, checkLibrary: false);
        if (descriptor.Owner != identity.Principal) RequireSpecial(SpecialAuthority.SecurityAdministrator);
        RequireAdoption(descriptor);
        new Ipc.Services.Work.JobLockStore(factory).RequireAccess(descriptor.Library, descriptor.Name, descriptor.ObjectType, AuthorityBit.ObjectExist);
    }

    public void RequireJob(Ipc.Core.Work.JobKey key, bool allowInactiveProfile = false)
    {
        if (OperationIdentity.Current is not { } identity) return;
        if (!allowInactiveProfile) Caller(identity.Principal);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(user_profile,user) FROM sys_jobs WHERE number=$number AND name=$name AND user=$user";
        command.Parameters.AddWithValue("$number", key.Number);
        command.Parameters.AddWithValue("$name", key.Name);
        command.Parameters.AddWithValue("$user", key.User);
        if (command.ExecuteScalar() as string != identity.Principal) RequireSpecial(SpecialAuthority.JobControl);
    }

    public void RequireRunAs(string profile)
    {
        if (OperationIdentity.Current is not { } identity) return;
        Caller(identity.Principal);
        if (profile != identity.Principal) RequireObject("QSYS", profile, ObjectType.UserProfile, Authorities.UseBits);
    }

    public void RequireProfileRead(string name)
    {
        if (OperationIdentity.Current is { AuthSessionId: { } session } current &&
            !new SessionTokenStore(factory, TimeProvider.System).IsActive(session, current.Principal))
            throw new CpfException("CPF9802", "The authentication session is expired or revoked.");
        if (OperationIdentity.Current is { } identity && !identity.Principal.Equals(name, StringComparison.OrdinalIgnoreCase))
            RequireSpecial(SpecialAuthority.SecurityAdministrator);
    }

    public void RequireSpecial(SpecialAuthority required, bool allowAdopted = true)
    {
        if (OperationIdentity.Current is not { } identity) return;
        var (profile, groups) = Caller(identity.Principal);
        var special = profile.SpecialAuthorities;
        var profiles = new UserProfileStore(factory, new PasswordPolicy(new SystemValueRegistry()));
        foreach (var group in groups) special |= profiles.TryGetForAuthentication(group)?.SpecialAuthorities ?? SpecialAuthority.None;
        if (allowAdopted)
            foreach (var owner in identity.AdoptedOwners()) special |= profiles.TryGetForAuthentication(owner)?.SpecialAuthorities ?? SpecialAuthority.None;
        if ((special & required) != required) Deny("QSYS", "*SYSTEM", "*ADMIN", AuthorityBit.None);
    }

    private (UserProfile Profile, IReadOnlyList<string> Groups) Caller(string name)
    {
        using var connection = factory.Open();
        var profile = RequireCaller(connection, null, name);
        var profiles = new UserProfileStore(factory, new PasswordPolicy(new SystemValueRegistry()));
        return (profile, profiles.EffectiveGroups(name));
    }

    // Recheck time-dependent credentials on a publisher's existing connection.
    // Opening another connection after transactional DDL would self-block in shared cache.
    internal UserProfile RequireCaller(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        if (OperationIdentity.Current?.AuthSessionId is { } session &&
            !new SessionTokenStore(factory, TimeProvider.System).IsActive(connection, transaction, session, name))
            throw new CpfException("CPF9802", "The authentication session is expired or revoked.");
        var profile = UserProfileStore.ReadForAuthentication(connection, transaction, name);
        if (profile is null || profile.Status != ProfileStatus.Enabled ||
            profile.PasswordExpires is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new CpfException("CPF9802", "The executing profile is unavailable or requires a password change.");
        if (EimMappingStore.ReadInternal(connection, transaction, name) is { } mapping && !mapping.AllowsExecution(DateTimeOffset.UtcNow))
            throw new CpfException("CPF9802", "The mapped directory account is disabled, unavailable or requires revalidation.");
        return profile;
    }

    private void Deny(string library, string name, string type, AuthorityBit required)
    {
        if (ObjectName.IsValid(library) && ObjectName.IsValid(name))
        {
            var visible = new Ipc.Services.Work.JobTemporaryLibrary(factory).Visible(new(library, name), type);
            library = visible.Library; name = visible.Name.Value;
        }
        throw new CpfException("CPF9802", $"Not authorized to {library}/{name} {type}.");
    }
    private static bool Has(AuthorityBit granted, AuthorityBit required) => (granted & required) == required;
}
