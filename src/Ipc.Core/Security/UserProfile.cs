namespace Ipc.Core.Security;

public enum ProfileStatus
{
    Enabled,
    Disabled,
    Expired,
    PasswordExpired,
}

public sealed class UserProfile
{
    public required string Name { get; set; }

    public UserClass UserClass { get; set; } = UserClass.User;

    public SpecialAuthority SpecialAuthorities { get; set; }

    public string? GroupProfile { get; set; }

    public string? Owner { get; set; }

    public string? Description { get; set; }

    public ProfileStatus Status { get; set; } = ProfileStatus.Enabled;

    public string? InitialMenu { get; set; }

    public string? InitialProgram { get; set; }

    public string? InitialCurrentLibrary { get; set; }

    public DateTimeOffset? PasswordChanged { get; set; }

    public DateTimeOffset? PasswordExpires { get; set; }

    public int DaysUsed { get; set; }

    public int SignOnAttempts { get; set; }

    public string? PasswordHash { get; set; }

    public int PasswordHashIterations { get; set; } = 210_000;

    public int Ccsid { get; set; } = 37;

    public string? CountryId { get; set; } = "US";

    public string? Locale { get; set; } = "US";
}

public static class ProfileNames
{
    public const string QSecOficer = "QSECOFR";
    public const string QSecurityAdministrator = "QSECADM";
    public const string QUser = "QUSER";
    public const string QService = "QSYS";
    public const string QPgmR = "QPGMR";
    public const string QSysopr = "QSYSOPR";
}