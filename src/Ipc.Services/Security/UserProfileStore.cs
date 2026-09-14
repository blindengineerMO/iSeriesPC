using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public sealed class UserProfileStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly PasswordPolicy _policy;

    public UserProfileStore(SqliteConnectionFactory factory, PasswordPolicy policy)
    {
        _factory = factory;
        _policy = policy;
    }

    public IReadOnlyList<string> SystemProfiles { get; } =
        new[] { ProfileNames.QSecOficer, ProfileNames.QSecurityAdministrator, "QSYS" };

    public void SeedDefaults()
    {
        var secofr = new UserProfile
        {
            Name = ProfileNames.QSecOficer,
            UserClass = UserClass.SecurityOfficer,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SecurityOfficer),
            Description = "IBM default security officer",
        };
        SetPassword(secofr, "11111111");
        secofr.Status = ProfileStatus.PasswordExpired;
        Upsert(secofr);

        var qsecadm = new UserProfile
        {
            Name = ProfileNames.QSecurityAdministrator,
            UserClass = UserClass.SecurityAdministrator,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SecurityAdministrator),
            Description = "IBM default security administrator",
        };
        Upsert(qsecadm);

        var qsysopr = new UserProfile
        {
            Name = ProfileNames.QSysopr,
            UserClass = UserClass.SystemOperator,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SystemOperator),
            Description = "IBM system operator",
        };
        Upsert(qsysopr);

        var qpgmr = new UserProfile
        {
            Name = ProfileNames.QPgmR,
            UserClass = UserClass.Programmer,
            Description = "IBM default programmer",
        };
        Upsert(qpgmr);

        var quser = new UserProfile
        {
            Name = ProfileNames.QUser,
            UserClass = UserClass.User,
            Description = "IBM user used by host services and database",
        };
        Upsert(quser);

        var qsys = new UserProfile
        {
            Name = ProfileNames.QService,
            UserClass = UserClass.SecurityOfficer,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SecurityOfficer),
            Description = "System service profile",
            Status = ProfileStatus.Disabled,
        };
        Upsert(qsys);
    }

    public bool Exists(string name) => TryGet(name) is not null;

    public UserProfile? TryGet(string name)
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, user_class, special_auth, group_profile, owner, description, " +
                          "status, initial_menu, initial_program, initial_curlib, password_changed, " +
                          "password_expires, days_used, signon_attempts, password_hash, hash_iterations, " +
                          "ccsid, country_id, locale FROM sys_profiles WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return FromReader(reader);
    }

    public UserProfile Get(string name) =>
        TryGet(name) ?? throw new CpfException("CPF2204", $"User profile {name} not found.");

    public IReadOnlyList<UserProfile> ListAll()
    {
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, user_class, special_auth, group_profile, owner, description, " +
                          "status, initial_menu, initial_program, initial_curlib, password_changed, " +
                          "password_expires, days_used, signon_attempts, password_hash, hash_iterations, " +
                          "ccsid, country_id, locale FROM sys_profiles ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var list = new List<UserProfile>();
        while (reader.Read())
        {
            list.Add(FromReader(reader));
        }

        return list;
    }

    public void Create(UserProfile profile)
    {
        if (Exists(profile.Name))
        {
            throw new CpfException("CPF2205", $"User profile {profile.Name} already exists.");
        }

        Upsert(profile);
    }

    public void Update(UserProfile profile) => Upsert(profile);

    public void Delete(string name)
    {
        if (SystemProfiles.Contains(name.ToUpperInvariant(), StringComparer.Ordinal))
        {
            throw new CpfException("CPF2283", $"User profile {name} cannot be deleted.");
        }

        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sys_profiles WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    public void SetPassword(UserProfile profile, string password)
    {
        var issues = _policy.Validate(password);
        if (issues.Count > 0)
        {
            throw new CpfException("CPF22M3", issues[0]);
        }

        profile.PasswordHash = PasswordHasher.Hash(password, profile.PasswordHashIterations);
        profile.PasswordChanged = DateTimeOffset.UtcNow;
        profile.PasswordExpires = profile.PasswordChanged.Value.AddDays(ExpirationIntervalDays());
        profile.Status = ProfileStatus.Enabled;
        Upsert(profile);
    }

    public void ChangePassword(string name, string current, string next)
    {
        var profile = Get(name);
        if (!PasswordHasher.Verify(current, profile.PasswordHash ?? string.Empty))
        {
            throw new CpfException("CPF22CD", "Current password is not correct.");
        }

        SetPassword(profile, next);
        Upsert(profile);
    }

    public bool VerifyPassword(string name, string password)
    {
        var profile = TryGet(name);
        return profile?.PasswordHash is not null &&
               PasswordHasher.Verify(password, profile.PasswordHash);
    }

    public void RecordSignOnFailure(string name)
    {
        var profile = TryGet(name);
        if (profile is null)
        {
            return;
        }

        profile.SignOnAttempts++;
        var maximum = MaximumSignOnAttempts();
        if (profile.SignOnAttempts >= maximum)
        {
            profile.Status = ProfileStatus.Disabled;
        }

        Upsert(profile);
    }

    public void RecordSignOnSuccess(string name)
    {
        var profile = TryGet(name);
        if (profile is null)
        {
            return;
        }

        profile.SignOnAttempts = 0;
        profile.DaysUsed++;
        Upsert(profile);
    }

    public IReadOnlyList<string> EffectiveGroups(string name)
    {
        var groups = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = TryGet(name)?.GroupProfile;
        while (current is not null && seen.Add(current))
        {
            groups.Add(current);
            current = TryGet(current)?.GroupProfile;
        }

        return groups;
    }

    private int ExpirationIntervalDays()
    {
        var store = new Sqlite.SystemValueStore(_factory);
        var registry = new SystemValueRegistry();
        store.LoadInto(registry);
        var value = registry.Get(SystemValueNames.PasswordExpirationInterval).Value;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var days)
            ? Math.Max(0, days)
            : 30;
    }

    private int MaximumSignOnAttempts()
    {
        var store = new Sqlite.SystemValueStore(_factory);
        var registry = new SystemValueRegistry();
        store.LoadInto(registry);
        var value = registry.Get(SystemValueNames.MaximumSignOnAttempts).Value;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var maximum)
            ? Math.Max(1, maximum)
            : 3;
    }

    private void Upsert(UserProfile profile)
    {
        profile.Name = profile.Name.ToUpperInvariant();
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sys_profiles (
                name, user_class, special_auth, group_profile, owner, description, status,
                initial_menu, initial_program, initial_curlib, password_changed, password_expires,
                days_used, signon_attempts, password_hash, hash_iterations, ccsid, country_id, locale)
            VALUES (
                $name, $class, $auth, $group, $owner, $description, $status,
                $menu, $pgm, $curlib, $changed, $expires,
                $days, $attempts, $hash, $iterations, $ccsid, $country, $locale)
            ON CONFLICT(name) DO UPDATE SET
                user_class = excluded.user_class,
                special_auth = excluded.special_auth,
                group_profile = excluded.group_profile,
                owner = excluded.owner,
                description = excluded.description,
                status = excluded.status,
                initial_menu = excluded.initial_menu,
                initial_program = excluded.initial_program,
                initial_curlib = excluded.initial_curlib,
                password_changed = excluded.password_changed,
                password_expires = excluded.password_expires,
                days_used = excluded.days_used,
                signon_attempts = excluded.signon_attempts,
                password_hash = excluded.password_hash,
                hash_iterations = excluded.hash_iterations,
                ccsid = excluded.ccsid,
                country_id = excluded.country_id,
                locale = excluded.locale;
            """;
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$class", UserClasses.Name(profile.UserClass));
        cmd.Parameters.AddWithValue("$auth", (int)profile.SpecialAuthorities);
        cmd.Parameters.AddWithValue("$group", (object?)profile.GroupProfile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)profile.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$description", (object?)profile.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", profile.Status.ToString());
        cmd.Parameters.AddWithValue("$menu", (object?)profile.InitialMenu ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pgm", (object?)profile.InitialProgram ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$curlib", (object?)profile.InitialCurrentLibrary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$changed",
            profile.PasswordChanged?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$expires",
            profile.PasswordExpires?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$days", profile.DaysUsed);
        cmd.Parameters.AddWithValue("$attempts", profile.SignOnAttempts);
        cmd.Parameters.AddWithValue("$hash", (object?)profile.PasswordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iterations", profile.PasswordHashIterations);
        cmd.Parameters.AddWithValue("$ccsid", profile.Ccsid);
        cmd.Parameters.AddWithValue("$country", (object?)profile.CountryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$locale", (object?)profile.Locale ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static UserProfile FromReader(System.Data.Common.DbDataReader reader)
    {
        string? N(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

        return new UserProfile
        {
            Name = reader.GetString(0),
            UserClass = UserClasses.Parse(reader.GetString(1)),
            SpecialAuthorities = (SpecialAuthority)reader.GetInt32(2),
            GroupProfile = N(3),
            Owner = N(4),
            Description = N(5),
            Status = Enum.TryParse<ProfileStatus>(reader.GetString(6), out var status) ? status : ProfileStatus.Enabled,
            InitialMenu = N(7),
            InitialProgram = N(8),
            InitialCurrentLibrary = N(9),
            PasswordChanged = ParseDate(N(10)),
            PasswordExpires = ParseDate(N(11)),
            DaysUsed = reader.GetInt32(12),
            SignOnAttempts = reader.GetInt32(13),
            PasswordHash = N(14),
            PasswordHashIterations = reader.GetInt32(15),
            Ccsid = reader.GetInt32(16),
            CountryId = N(17),
            Locale = N(18),
        };
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        value is null
            ? null
            : DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
}