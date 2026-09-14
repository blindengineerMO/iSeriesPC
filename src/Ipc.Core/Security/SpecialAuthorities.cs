namespace Ipc.Core.Security;

[Flags]
public enum SpecialAuthority
{
    None = 0,
    AllObject = 1 << 0,
    Audit = 1 << 1,
    IoSystemConfig = 1 << 2,
    JobControl = 1 << 3,
    SaveSystem = 1 << 4,
    SecurityAdministrator = 1 << 5,
    Service = 1 << 6,
    SpoolControl = 1 << 7,
    SystemOperator = 1 << 8,
    PasswordControl = 1 << 9,
}

public enum UserClass
{
    User,
    Programmer,
    SystemOperator,
    SecurityAdministrator,
    SecurityOfficer,
}

public static class SpecialAuthorities
{
    public static SpecialAuthority Parse(string value) => value.ToUpperInvariant() switch
    {
        "*ALLOBJ" => SpecialAuthority.AllObject,
        "*AUDIT" => SpecialAuthority.Audit,
        "*IOSYSCFG" => SpecialAuthority.IoSystemConfig,
        "*JOBCTL" => SpecialAuthority.JobControl,
        "*SAVSYS" => SpecialAuthority.SaveSystem,
        "*SECADM" => SpecialAuthority.SecurityAdministrator,
        "*SERVICE" => SpecialAuthority.Service,
        "*SPLCTL" => SpecialAuthority.SpoolControl,
        "*SYSOPR" => SpecialAuthority.SystemOperator,
        "*PWDCTL" => SpecialAuthority.PasswordControl,
        "*NONE" => SpecialAuthority.None,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown special authority '{value}'."),
    };

    public static string Name(SpecialAuthority authority) => authority switch
    {
        SpecialAuthority.AllObject => "*ALLOBJ",
        SpecialAuthority.Audit => "*AUDIT",
        SpecialAuthority.IoSystemConfig => "*IOSYSCFG",
        SpecialAuthority.JobControl => "*JOBCTL",
        SpecialAuthority.SaveSystem => "*SAVSYS",
        SpecialAuthority.SecurityAdministrator => "*SECADM",
        SpecialAuthority.Service => "*SERVICE",
        SpecialAuthority.SpoolControl => "*SPLCTL",
        SpecialAuthority.SystemOperator => "*SYSOPR",
        SpecialAuthority.PasswordControl => "*PWDCTL",
        _ => "*NONE",
    };

    public static IReadOnlyList<SpecialAuthority> All = Enum.GetValues<SpecialAuthority>()
        .Where(v => v != SpecialAuthority.None)
        .ToArray();
}

public static class UserClasses
{
    public static UserClass Parse(string value) => value.ToUpperInvariant() switch
    {
        "*USER" => UserClass.User,
        "*PGMR" => UserClass.Programmer,
        "*SYSOPR" => UserClass.SystemOperator,
        "*SECADM" => UserClass.SecurityAdministrator,
        "*SECOFR" => UserClass.SecurityOfficer,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown user class '{value}'."),
    };

    public static string Name(UserClass userClass) => userClass switch
    {
        UserClass.User => "*USER",
        UserClass.Programmer => "*PGMR",
        UserClass.SystemOperator => "*SYSOPR",
        UserClass.SecurityAdministrator => "*SECADM",
        UserClass.SecurityOfficer => "*SECOFR",
        _ => "*USER",
    };

    public static SpecialAuthority BaselineAuthorities(UserClass userClass) => userClass switch
    {
        UserClass.SecurityOfficer =>
            SpecialAuthority.AllObject | SpecialAuthority.SecurityAdministrator |
            SpecialAuthority.JobControl | SpecialAuthority.SaveSystem | SpecialAuthority.Service |
            SpecialAuthority.SpoolControl | SpecialAuthority.SystemOperator |
            SpecialAuthority.IoSystemConfig | SpecialAuthority.Audit | SpecialAuthority.PasswordControl,
        UserClass.SecurityAdministrator =>
            SpecialAuthority.SecurityAdministrator | SpecialAuthority.JobControl,
        UserClass.SystemOperator =>
            SpecialAuthority.JobControl | SpecialAuthority.SaveSystem | SpecialAuthority.SystemOperator,
        UserClass.Programmer => SpecialAuthority.None,
        _ => SpecialAuthority.None,
    };
}