using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;

namespace Ipc.Services.Security;

public sealed record SignOnResult(
    bool Success,
    UserProfile? Profile,
    string? Reason,
    bool MustChangePassword = false);

public sealed class SecurityService
{
    private readonly UserProfileStore _profiles;
    private readonly PasswordPolicy _policy;
    private readonly AuthorityEngine _authority;
    private readonly SystemValueRegistry _registry;

    public SecurityService(
        UserProfileStore profiles,
        PasswordPolicy policy,
        AuthorityEngine authority,
        SystemValueRegistry registry)
    {
        _profiles = profiles;
        _policy = policy;
        _authority = authority;
        _registry = registry;
    }

    public UserProfileStore Profiles => _profiles;

    public PasswordPolicy PasswordPolicy => _policy;

    public AuthorityEngine Authority => _authority;

    public SignOnResult Authenticate(string name, string password)
    {
        var profile = _profiles.TryGet(name);
        if (profile is null)
        {
            return new SignOnResult(false, null, $"User profile {name.ToUpperInvariant()} not found.");
        }

        if (profile.Status == ProfileStatus.Disabled)
        {
            return new SignOnResult(false, profile, $"User profile {name.ToUpperInvariant()} is disabled.");
        }

        var securityLevel = _authority.SecurityLevel;

        if (securityLevel >= 20)
        {
            if (!_profiles.VerifyPassword(name, password))
            {
                _profiles.RecordSignOnFailure(name);
                return new SignOnResult(false, profile, "Password is not correct.");
            }

            _profiles.RecordSignOnSuccess(name);

            if (profile.Status == ProfileStatus.PasswordExpired ||
                PasswordExpired(profile))
            {
                return new SignOnResult(true, profile, null, MustChangePassword: true);
            }
        }
        else
        {
            _profiles.RecordSignOnSuccess(name);
        }

        return new SignOnResult(true, profile, null);
    }

    public void CreateProfile(UserProfile profile) => _profiles.Create(profile);

    public void ChangePassword(string name, string current, string next) =>
        _profiles.ChangePassword(name, current, next);

    private bool PasswordExpired(UserProfile profile)
    {
        if (profile.PasswordExpires is not { } expires)
        {
            return false;
        }

        return DateTimeOffset.UtcNow > expires;
    }
}