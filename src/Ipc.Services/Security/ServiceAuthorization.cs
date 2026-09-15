using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

/// <summary>Checks server-established callers at service boundaries. No identity denotes trusted host code.</summary>
public sealed class ServiceAuthorization(SqliteConnectionFactory factory)
{
    public void RequireObject(string library, string name, string type, AuthorityBit required, bool checkLibrary = true, bool acquireLocks = true)
    {
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
        if (descriptor is null || !Has(Effective(engine, profile, groups, descriptor), required))
            Deny(library, name, type, required);
        if (acquireLocks)
        {
            var locks = new Ipc.Services.Work.JobLockStore(factory);
            if (checkLibrary && !(library == "QSYS" && type == ObjectType.Library))
                locks.RequireAccess("QSYS", library, ObjectType.Library, Authorities.UseBits);
            locks.RequireAccess(library, name, type, required);
        }
    }

    internal void RequireSnapshot(ObjectDescriptor descriptor, AuthorityBit required)
    {
        if (OperationIdentity.Current is not { } identity) return;
        var (profile, groups) = Caller(identity.Principal); var objects = new SqliteObjectStore(factory); var engine = new AuthorityEngine(factory, objects);
        var library = objects.GetForAuthorization("QSYS", descriptor.Library, ObjectType.Library);
        if (library is null || !Has(Effective(engine, profile, groups, library), AuthorityBit.ObjectOperate) || !Has(Effective(engine, profile, groups, descriptor), required))
            Deny(descriptor.Library, descriptor.Name, descriptor.ObjectType, required);
    }

    private static AuthorityBit Effective(AuthorityEngine engine, UserProfile profile, IReadOnlyList<string> groups, ObjectDescriptor descriptor)
    {
        var bits = engine.ComputeEffective(profile, groups, descriptor);
        if (OperationIdentity.Current is { } identity)
            foreach (var owner in identity.AdoptedOwners().Distinct(StringComparer.Ordinal)) bits |= engine.AdoptedAuthority(owner, descriptor);
        return bits;
    }

    public void RequireAdoption(ObjectDescriptor program)
    {
        if (OperationIdentity.Current is { } identity && program.AdoptsOwnerAuthority && program.Owner != identity.Principal)
            RequireObject("QSYS", program.Owner, ObjectType.UserProfile, Authorities.UseBits);
    }

    public void RequireCreate(ObjectDescriptor descriptor)
    {
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
        if (OperationIdentity.Current?.AuthSessionId is { } session &&
            !new SessionTokenStore(factory, TimeProvider.System).IsActive(session, name))
            throw new CpfException("CPF9802", "The authentication session is expired or revoked.");
        // Internal identity lookup must not recursively authorize the lookup itself.
        var profiles = new UserProfileStore(factory, new PasswordPolicy(new SystemValueRegistry()));
        var profile = profiles.TryGetForAuthentication(name);
        if (profile is null || profile.Status != ProfileStatus.Enabled ||
            profile.PasswordExpires is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new CpfException("CPF9802", "The executing profile is unavailable or requires a password change.");
        if (new EimMappingStore(factory).GetInternal(name) is { } mapping && !mapping.AllowsExecution(DateTimeOffset.UtcNow))
            throw new CpfException("CPF9802", "The mapped directory account is disabled, unavailable or requires revalidation.");
        return (profile, profiles.EffectiveGroups(name));
    }

    private void Deny(string library, string name, string type, AuthorityBit required)
    {
        throw new CpfException("CPF9802", $"Not authorized to {library}/{name} {type}.");
    }
    private static bool Has(AuthorityBit granted, AuthorityBit required) => (granted & required) == required;
}
