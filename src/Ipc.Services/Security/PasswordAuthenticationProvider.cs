namespace Ipc.Services.Security;

public enum CredentialStatus { Valid, Invalid, PasswordExpired, AccountUnavailable, ProviderUnavailable }
public readonly record struct CredentialVerification(CredentialStatus Status, string? CredentialVersion = null);

/// <summary>Verifies credentials only; profile state and job authorization remain server-owned.</summary>
public interface IPasswordAuthenticationProvider
{
    CredentialVerification Verify(string account, string password);
}

public sealed class ProfilePasswordProvider(UserProfileStore profiles) : IPasswordAuthenticationProvider
{
    public CredentialVerification Verify(string account, string password)
    {
        var profile = profiles.TryGetForAuthentication(account);
        return new(PasswordHasher.Verify(password, profile?.PasswordHash ?? "") ? CredentialStatus.Valid : CredentialStatus.Invalid, profile?.PasswordHash);
    }
}
