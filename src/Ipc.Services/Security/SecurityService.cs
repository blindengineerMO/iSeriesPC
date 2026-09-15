using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.System;

namespace Ipc.Services.Security;

public sealed record SignOnResult(
    bool Success,
    UserProfile? Profile,
    string? Reason,
    bool MustChangePassword = false,
    bool MustVerifyMfa = false)
{
    internal string? MfaRevision { get; init; }
    public SessionCredential? Session { get; init; }
}

public sealed class SecurityService : IDisposable
{
    private readonly UserProfileStore _profiles;
    private readonly PasswordPolicy _policy;
    private readonly AuthorityEngine _authority;
    private readonly SystemValueRegistry _registry;
    private readonly Ipc.Services.Events.DurableEventStore? _events;
    private readonly IPasswordAuthenticationProvider _local;
    private readonly IPasswordAuthenticationProvider _pam;
    private readonly Dictionary<string, string> _pamAccounts;
    private readonly bool _bindTerminalPam;
    private readonly bool _pamRequireProfilePassword;
    private readonly EimService? _eim;
    private readonly IPasswordAuthenticationProvider _sssd;
    private readonly SecretProtector _protector;
    private readonly MfaStore _mfa;
    private readonly SessionTokenStore _sessions;

    public SecurityService(
        UserProfileStore profiles,
        PasswordPolicy policy,
        AuthorityEngine authority,
        SystemValueRegistry registry, Ipc.Services.Events.DurableEventStore? events = null,
        Ipc.Services.Configuration.AuthenticationConfig? authentication = null, IPasswordAuthenticationProvider? pam = null,
        EimService? eim = null, IPasswordAuthenticationProvider? sssd = null, TimeProvider? clock = null)
    {
        _profiles = profiles;
        _policy = policy;
        _authority = authority;
        _registry = registry;
        _events = events;
        clock ??= TimeProvider.System;
        _protector = new SecretProtector(profiles.Factory);
        _mfa = new MfaStore(profiles.Factory, _protector, clock);
        _sessions = new SessionTokenStore(profiles.Factory, clock);
        authentication ??= new();
        _local = new ProfilePasswordProvider(profiles);
        _pam = pam ?? new PamPasswordProvider(authentication.PamService, TimeSpan.FromSeconds(15), authentication.PamConfigurationDirectory);
        _sssd = sssd ?? new PamPasswordProvider(authentication.SssdPamService, TimeSpan.FromSeconds(15), authentication.PamConfigurationDirectory);
        _eim = eim;
        _bindTerminalPam = authentication.BindTerminalPamToUnixAccount;
        _pamRequireProfilePassword = authentication.PamRequireProfilePassword;
        _pamAccounts = new(StringComparer.Ordinal);
        foreach (var (profile, account) in authentication.PamAccounts)
        {
            if (!ObjectName.IsValid(profile.ToUpperInvariant()) || string.IsNullOrWhiteSpace(account) ||
                account.Length > 256 || account.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                _pamAccounts.ContainsValue(account) || !_pamAccounts.TryAdd(profile.ToUpperInvariant(), account))
                throw new ArgumentException("PAM mappings must uniquely map valid profiles to Linux accounts.");
        }
    }

    public UserProfileStore Profiles => _profiles;

    public PasswordPolicy PasswordPolicy => _policy;

    public AuthorityEngine Authority => _authority;

    public SignOnResult Authenticate(string name, string password, string? unixAccount = null, bool terminal = false, string? verificationCode = null)
    {
        var provider = _eim?.Mappings.GetInternal(name) is not null ? "sssd-kerberos" :
            _pamAccounts.ContainsKey(name.ToUpperInvariant()) ? "pam" : "profile";
        var revision = _mfa.Revision(name.ToUpperInvariant());
        var result = AuthenticateCore(name, password, unixAccount, terminal);
        if (result.Success && !result.MustChangePassword)
        {
            if (revision != _mfa.Revision(result.Profile!.Name))
                result = new(false, null, "Authentication settings changed; sign on again.");
            else if (revision is not null)
            {
                if (string.IsNullOrEmpty(verificationCode))
                    result = result with { Success = false, MustVerifyMfa = true, Reason = "Authenticator or recovery code required." };
                else if (_mfa.Verify(result.Profile.Name, verificationCode, revision) != MfaVerification.Accepted)
                    result = new(false, null, "Authenticator or recovery code rejected or temporarily unavailable.");
            }
            result = result with { MfaRevision = revision };
        }
        _events?.Append("security.authentication", new { attemptedProfile = name.ToUpperInvariant(),
            provider,
            result.Success, result.MustChangePassword, result.MustVerifyMfa }, principal: Ipc.Services.Events.OperationIdentity.Current?.Principal ??
                (result.Success ? result.Profile!.Name : "*UNAUTHENTICATED"));
        return result;
    }

    public SignOnResult OpenSession(string name, string password, string? verificationCode = null,
        string? unixAccount = null, bool terminal = false)
    {
        var result = Authenticate(name, password, unixAccount, terminal, verificationCode);
        if (!result.Success || result.MustChangePassword) return result;
        try { return result with { Session = _sessions.Issue(result.Profile!, result.MfaRevision) }; }
        catch (CpfException) { return new(false, null, "Authentication changed; sign on again."); }
    }

    public SignOnResult ResumeSession(string token, string? unixAccount = null, bool terminal = false)
    {
        var session = _sessions.Resolve(token);
        if (session is null) return new(false, null, "Session is expired or revoked.");
        var profile = _profiles.TryGetForAuthentication(session.Profile);
        var mapping = _eim?.Mappings.GetInternal(session.Profile);
        var account = mapping?.LinuxAccount ?? _pamAccounts.GetValueOrDefault(session.Profile);
        if (terminal && _bindTerminalPam && account is not null && account != unixAccount)
            return new(false, null, "Linux account does not match the configured profile mapping.");
        if (mapping is not null && _eim!.CheckCurrent(mapping) != DirectoryIdentityState.Active)
            return new(false, null, "Directory identity is unavailable.");
        return profile is null ? new(false, null, "Profile is unavailable.") : new(true, profile, null) { Session = session };
    }

    public void RevokeSession(string token) => _sessions.RevokeToken(token);
    public bool IsSessionActive(string id, string profile) => _sessions.IsActive(id, profile, touch: true);

    public MfaEnrollment BeginMfaEnrollment(string name, string password, string? verificationCode = null,
        string? unixAccount = null, bool terminal = false)
    {
        RequireSelf(name);
        var proof = Authenticate(name, password, unixAccount, terminal, verificationCode);
        if (!proof.Success || proof.MustChangePassword) throw new CpfException("IPC0102", "Current password and enrolled factor are required.");
        return _mfa.Begin(proof.Profile!, "iSeriesPC", proof.MfaRevision);
    }

    public MfaConfirmation ConfirmMfaEnrollment(string enrollmentToken, string code) => _mfa.Confirm(enrollmentToken, code);

    public void DisableMfa(string name, string password, string verificationCode, string? unixAccount = null, bool terminal = false)
    {
        RequireSelf(name);
        var proof = Authenticate(name, password, unixAccount, terminal, verificationCode);
        if (!proof.Success || proof.MustChangePassword) throw new CpfException("IPC0102", "Current password and enrolled factor are required.");
        _mfa.Disable(proof.Profile!, proof.MfaRevision);
    }

    private static void RequireSelf(string name)
    {
        if (Ipc.Services.Events.OperationIdentity.Current is { } identity && !identity.Principal.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new CpfException("CPF9802", "Authentication settings belong to the signed-on profile.");
    }

    public void Dispose() => _protector.Dispose();

    private SignOnResult AuthenticateCore(string name, string password, string? unixAccount, bool terminal)
    {
        if (name.Length > 10 || password.Length > 128 || password.Any(char.IsControl))
            return new SignOnResult(false, null, "Invalid sign-on credentials.");
        var profile = _profiles.TryGetForAuthentication(name);
        if (profile is null)
        {
            return new SignOnResult(false, null, $"User profile {name.ToUpperInvariant()} not found.");
        }

        if (profile.Status is not (ProfileStatus.Enabled or ProfileStatus.PasswordExpired))
        {
            return new SignOnResult(false, profile, $"User profile {name.ToUpperInvariant()} is disabled.");
        }

        var securityLevel = _authority.SecurityLevel;

        var usesPam = _pamAccounts.TryGetValue(profile.Name, out var account);
        var mapping = _eim?.Mappings.GetInternal(profile.Name);
        if (mapping is not null)
        {
            if (usesPam || !mapping.Enabled || _eim!.CheckCurrent(mapping) != DirectoryIdentityState.Active)
                return new SignOnResult(false, null, "Enterprise identity is ambiguous, disabled or unavailable.");
            usesPam = true;
            account = mapping.LinuxAccount;
        }
        if (usesPam && (mapping is not null || !_pamRequireProfilePassword) &&
            (profile.Status == ProfileStatus.PasswordExpired || PasswordExpired(profile)))
            return new SignOnResult(false, null, "The mapped profile requires security-administrator recovery.");
        if (usesPam && terminal && _bindTerminalPam && account != unixAccount)
            return new SignOnResult(false, null, "Linux account does not match the configured profile mapping.");
        if (securityLevel >= 20 || usesPam)
        {
            var verification = mapping is not null ? _sssd.Verify(account!, password) :
                usesPam ? _pam.Verify(account!, password) : _local.Verify(name, password);
            if (verification.Status == CredentialStatus.PasswordExpired)
                return new SignOnResult(false, null, "Linux password expired; change it through the Linux account provider before signing on.");
            if (verification.Status is CredentialStatus.AccountUnavailable or CredentialStatus.ProviderUnavailable)
                return new SignOnResult(false, null, "Authentication provider or account is unavailable.");
            if (verification.Status == CredentialStatus.Valid && mapping is null && usesPam && _pamRequireProfilePassword)
            {
                verification = _local.Verify(name, password);
                if (verification.CredentialVersion != profile.PasswordHash) verification = new(CredentialStatus.Invalid);
            }
            if (verification.Status != CredentialStatus.Valid || !usesPam && verification.CredentialVersion != profile.PasswordHash)
            {
                _profiles.RecordSignOnFailure(name);
                return new SignOnResult(false, profile, "Password is not correct.");
            }

            if (!_profiles.RecordVerifiedSignOn(profile))
                return new SignOnResult(false, null, "Profile or credentials changed; sign on again.");
            if (mapping is not null && (_eim!.Mappings.GetInternal(profile.Name) is not { } current ||
                current.Revision != mapping.Revision || !current.AllowsExecution(DateTimeOffset.UtcNow)))
                return new SignOnResult(false, null, "Enterprise identity changed; sign on again.");

            if ((!usesPam || mapping is null && _pamRequireProfilePassword) &&
                (profile.Status == ProfileStatus.PasswordExpired || PasswordExpired(profile)))
            {
                return new SignOnResult(true, profile, null, MustChangePassword: true);
            }
        }
        else
        {
            if (!_profiles.RecordVerifiedSignOn(profile))
                return new SignOnResult(false, null, "Profile is unavailable.");
        }

        return new SignOnResult(true, profile, null, profile.Status == ProfileStatus.PasswordExpired || PasswordExpired(profile));
    }

    public void CreateProfile(UserProfile profile) => _profiles.Create(profile);

    public void ChangePassword(string name, string current, string next, string? verificationCode = null)
    {
        var success = false;
        try
        {
            if (_eim?.Mappings.GetInternal(name) is not null ||
                _pamAccounts.ContainsKey(name.ToUpperInvariant()) && !_pamRequireProfilePassword)
                throw new CpfException("CPF22CD", "Change this password through the Linux account provider.");
            if (_pamAccounts.TryGetValue(name.ToUpperInvariant(), out var account) && _pam.Verify(account, next).Status != CredentialStatus.Valid)
                throw new CpfException("CPF22CD", "Update the Linux password first, then synchronize the profile password using its current password.");
            _profiles.ChangePassword(name, current, next, profile =>
            {
                var revision = _mfa.Revision(profile.Name);
                if (revision is not null && _mfa.Verify(profile.Name, verificationCode ?? "", revision) != MfaVerification.Accepted)
                    throw new CpfException("IPC0102", "Authenticator or recovery code is required to change the password.");
                return revision;
            });
            success = true;
        }
        finally
        {
            _events?.Append("security.password.changed", new { profile = name.ToUpperInvariant(), success },
                principal: Ipc.Services.Events.OperationIdentity.Current?.Principal ?? (success ? name.ToUpperInvariant() : "*UNAUTHENTICATED"));
        }
    }

    private bool PasswordExpired(UserProfile profile)
    {
        if (profile.PasswordExpires is not { } expires)
        {
            return false;
        }

        return DateTimeOffset.UtcNow > expires;
    }
}
