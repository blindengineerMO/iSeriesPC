using Ipc.Core.Messages;
using Ipc.Core.Security;
using Ipc.Core.System;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Security;

public sealed class UserProfileStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly PasswordPolicy _policy;
    internal SqliteConnectionFactory Factory => _factory;

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
        if (!Exists(secofr.Name))
        {
            var initial = new BootstrapCredentialStore(_factory).Prepare(_policy);
            secofr.PasswordHash = PasswordHasher.Hash(initial, secofr.PasswordHashIterations);
            secofr.PasswordChanged = DateTimeOffset.UtcNow;
            secofr.PasswordExpires = secofr.PasswordChanged.Value.AddDays(ExpirationIntervalDays());
            secofr.Status = ProfileStatus.PasswordExpired;
            Upsert(secofr, createOnly: true);
        }
        else if (Get(secofr.Name).Status != ProfileStatus.PasswordExpired)
            new BootstrapCredentialStore(_factory).Remove();

        var qsecadm = new UserProfile
        {
            Name = ProfileNames.QSecurityAdministrator,
            UserClass = UserClass.SecurityAdministrator,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SecurityAdministrator),
            Description = "IBM default security administrator",
        };
        Upsert(qsecadm, createOnly: true);

        var qsysopr = new UserProfile
        {
            Name = ProfileNames.QSysopr,
            UserClass = UserClass.SystemOperator,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SystemOperator),
            Description = "IBM system operator",
        };
        Upsert(qsysopr, createOnly: true);

        var qpgmr = new UserProfile
        {
            Name = ProfileNames.QPgmR,
            UserClass = UserClass.Programmer,
            Description = "IBM default programmer",
        };
        Upsert(qpgmr, createOnly: true);

        var quser = new UserProfile
        {
            Name = ProfileNames.QUser,
            UserClass = UserClass.User,
            Description = "IBM user used by host services and database",
        };
        Upsert(quser, createOnly: true);

        var qsys = new UserProfile
        {
            Name = ProfileNames.QService,
            UserClass = UserClass.SecurityOfficer,
            SpecialAuthorities = UserClasses.BaselineAuthorities(UserClass.SecurityOfficer),
            Description = "System service profile",
            Status = ProfileStatus.Disabled,
        };
        Upsert(qsys, createOnly: true);
    }

    public bool Exists(string name) => TryGet(name) is not null;

    public string ResetAdministratorCredential()
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        var bootstrap = new BootstrapCredentialStore(_factory);
        var path = bootstrap.PathName ?? throw new InvalidOperationException("Recovery requires a persistent catalog.");
        bootstrap.Remove();
        var password = bootstrap.Prepare(_policy);
        var profile = Get(ProfileNames.QSecOficer);
        profile.PasswordHash = PasswordHasher.Hash(password, profile.PasswordHashIterations);
        profile.PasswordChanged = DateTimeOffset.UtcNow;
        profile.PasswordExpires = null;
        profile.Status = ProfileStatus.PasswordExpired;
        profile.SignOnAttempts = 0;
        Upsert(profile);
        using (var connection = _factory.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using var reset = connection.CreateCommand();
            reset.Transaction = transaction;
            reset.CommandText = "DELETE FROM sys_mfa_enrollments WHERE profile='QSECOFR'; DELETE FROM sys_mfa WHERE profile='QSECOFR'";
            reset.ExecuteNonQuery();
            MfaStore.Audit(connection, transaction, "security.administrator.recovered", profile.Name, true, DateTimeOffset.UtcNow);
            transaction.Commit();
        }
        return path;
    }

    public UserProfile? TryGet(string name)
    {
        new ServiceAuthorization(_factory).RequireProfileRead(name);
        return TryGetForAuthentication(name);
    }

    internal UserProfile? TryGetForAuthentication(string name)
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
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
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
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        Upsert(profile, createOnly: true, rejectExisting: true);
    }

    public void Update(UserProfile profile)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        Upsert(profile);
    }

    public void SaveSettings(UserProfile profile, bool create, string? password = null, bool removePassword = false)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        if (password is not null && removePassword) throw new ArgumentException("Choose a password or password removal.");
        if (password is not null)
        {
            var issues = _policy.Validate(password); if (issues.Count > 0) throw new CpfException("CPF22M3", issues[0]);
            profile.PasswordHashIterations = 210_000; profile.PasswordHash = PasswordHasher.Hash(password, profile.PasswordHashIterations);
            profile.PasswordChanged = DateTimeOffset.UtcNow;
            var interval = ExpirationIntervalDays(); profile.PasswordExpires = interval == 0 ? null : profile.PasswordChanged.Value.AddDays(interval);
            profile.SignOnAttempts = 0;
        }
        else if (removePassword) { profile.PasswordHash = null; profile.PasswordChanged = DateTimeOffset.UtcNow; profile.PasswordExpires = null; profile.SignOnAttempts = 0; }
        new Ipc.Services.Work.JobLockStore(_factory).RequireAccess("QSYS", profile.Name.ToUpperInvariant(), Ipc.Core.Objects.ObjectType.UserProfile, Ipc.Core.Objects.AuthorityBit.ObjectManagement);
        Upsert(profile, createOnly: create, rejectExisting: create, preserveCredentials: !create && password is null && !removePassword);
    }

    public void Delete(string name)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        if (SystemProfiles.Contains(name.ToUpperInvariant(), StringComparer.Ordinal))
        {
            throw new CpfException("CPF2283", $"User profile {name} cannot be deleted.");
        }

        new ObjectCatalogOperations(_factory).Delete(new Ipc.Core.Objects.QualifiedName("QSYS", name.ToUpperInvariant()), Ipc.Core.Objects.ObjectType.UserProfile);
    }

    public void SetPassword(UserProfile profile, string password)
    {
        new ServiceAuthorization(_factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        SetPasswordCore(profile, password);
    }

    private void SetPasswordCore(UserProfile profile, string password, string? expectedHash = null, string? expectedMfa = null)
    {
        var issues = _policy.Validate(password);
        if (issues.Count > 0) throw new CpfException("CPF22M3", issues[0]);
        var hash = PasswordHasher.Hash(password, profile.PasswordHashIterations);
        var changed = DateTimeOffset.UtcNow;
        var interval = ExpirationIntervalDays();
        DateTimeOffset? expires = interval == 0 ? null : changed.AddDays(interval);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sys_profiles SET password_hash=$hash, hash_iterations=$iterations,
                password_changed=$changed, password_expires=$expires, status='Enabled', signon_attempts=0
            WHERE name=$name
            """ + (expectedHash is null ? "" : " AND password_hash=$expected AND status IN ('Enabled','PasswordExpired') AND $mfa IS (SELECT revision FROM sys_mfa WHERE profile=$name)");
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$iterations", profile.PasswordHashIterations);
        cmd.Parameters.AddWithValue("$changed", changed.ToString("o"));
        cmd.Parameters.AddWithValue("$expires", (object?)expires?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", profile.Name.ToUpperInvariant());
        if (expectedHash is not null) cmd.Parameters.AddWithValue("$expected", expectedHash);
        if (expectedHash is not null) cmd.Parameters.AddWithValue("$mfa", (object?)expectedMfa ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() != 1) throw new CpfException("CPF22CD", "Profile or credentials changed; sign on again.");
        profile.PasswordHash = hash;
        profile.PasswordChanged = changed;
        profile.PasswordExpires = expires;
        profile.Status = ProfileStatus.Enabled;
        profile.SignOnAttempts = 0;
        if (profile.Name.Equals(ProfileNames.QSecOficer, StringComparison.OrdinalIgnoreCase))
            new BootstrapCredentialStore(_factory).Remove();
    }

    public void ChangePassword(string name, string current, string next)
        => ChangePassword(name, current, next, profile =>
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sys_mfa WHERE profile=$profile";
            command.Parameters.AddWithValue("$profile", profile.Name);
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new CpfException("IPC0102", "Use MFA-verified password change.");
            return null;
        });

    internal void ChangePassword(string name, string current, string next, Func<UserProfile, string?> verifyFactor)
    {
        var profile = Get(name);
        if (profile.Status is not (ProfileStatus.Enabled or ProfileStatus.PasswordExpired))
            throw new CpfException("CPF22CD", "Profile is unavailable; contact a security administrator.");
        if (!PasswordHasher.Verify(current, profile.PasswordHash ?? string.Empty))
        {
            RecordSignOnFailure(name);
            throw new CpfException("CPF22CD", "Current password is not correct.");
        }
        SetPasswordCore(profile, next, profile.PasswordHash, verifyFactor(profile));
    }

    public bool VerifyPassword(string name, string password)
    {
        var profile = TryGet(name);
        return profile?.PasswordHash is not null && PasswordHasher.Verify(password, profile.PasswordHash);
    }

    public void RecordSignOnFailure(string name)
    {
        new ServiceAuthorization(_factory).RequireProfileRead(name);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sys_profiles SET signon_attempts=signon_attempts+1,
                status=CASE WHEN signon_attempts+1 >= $maximum THEN 'Disabled' ELSE status END
            WHERE name=$name AND status IN ('Enabled','PasswordExpired')
            """;
        cmd.Parameters.AddWithValue("$maximum", MaximumSignOnAttempts());
        cmd.Parameters.AddWithValue("$name", name.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    public void RecordSignOnSuccess(string name)
    {
        new ServiceAuthorization(_factory).RequireProfileRead(name);
        var profile = TryGetForAuthentication(name);
        if (profile is not null) RecordVerifiedSignOn(profile);
    }

    internal bool RecordVerifiedSignOn(UserProfile snapshot)
    {
        // Only counters change. A concurrent disable, password reset or expiry must win.
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE sys_profiles SET signon_attempts=0, days_used=days_used+1
            WHERE name=$name AND status=$status AND status IN ('Enabled','PasswordExpired')
              AND password_hash IS $hash AND password_expires IS $expires
            """;
        cmd.Parameters.AddWithValue("$name", snapshot.Name);
        cmd.Parameters.AddWithValue("$status", snapshot.Status.ToString());
        cmd.Parameters.AddWithValue("$hash", (object?)snapshot.PasswordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expires", (object?)snapshot.PasswordExpires?.ToString("o") ?? DBNull.Value);
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<string> EffectiveGroups(string name)
    {
        var groups = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        new ServiceAuthorization(_factory).RequireProfileRead(name);
        var current = TryGetForAuthentication(name)?.GroupProfile;
        while (current is not null && seen.Add(current))
        {
            groups.Add(current);
            current = TryGetForAuthentication(current)?.GroupProfile;
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

    private void Upsert(UserProfile profile, bool createOnly = false, bool rejectExisting = false, bool preserveCredentials = false)
    {
        profile.Name = profile.Name.ToUpperInvariant();
        if (!Ipc.Core.Objects.ObjectName.IsValid(profile.Name))
            throw new ArgumentException("A valid profile name is required.", nameof(profile));
        if (profile.Owner is not null && !Ipc.Core.Objects.ObjectName.IsValid(profile.Owner))
            throw new ArgumentException("A valid owner profile name is required.", nameof(profile));
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!createOnly)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT count(*) FROM sys_profiles WHERE name=$name";
            exists.Parameters.AddWithValue("$name", profile.Name);
            if (Convert.ToInt64(exists.ExecuteScalar()) != 1)
                throw new CpfException("CPF2204", $"User profile {profile.Name} not found.");
        }
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
        if (preserveCredentials)
            foreach (var column in new[] { "password_hash", "hash_iterations", "password_changed", "password_expires", "signon_attempts", "days_used" })
                cmd.CommandText = cmd.CommandText.Replace(column + " = excluded." + column, column + " = sys_profiles." + column, StringComparison.Ordinal);
        if (createOnly)
            cmd.CommandText = cmd.CommandText[..cmd.CommandText.IndexOf("ON CONFLICT", StringComparison.Ordinal)] +
                "ON CONFLICT(name) DO NOTHING;";
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
        var changed = cmd.ExecuteNonQuery();
        if (rejectExisting && changed == 0)
            throw new CpfException("CPF2205", $"User profile {profile.Name} already exists.");
        transaction.Commit();
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
            Status = Enum.TryParse<ProfileStatus>(reader.GetString(6), out var status) ? status : ProfileStatus.Disabled,
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
