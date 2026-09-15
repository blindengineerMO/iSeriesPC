using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Security;

public enum DirectoryIdentityState { Unknown, Active, Disabled, Missing, IdentityMismatch, Unavailable }
public sealed record EimMapping(string Profile, string Directory, string DistinguishedName, string EntryId,
    string Principal, string LinuxAccount, string Revision = "", bool Enabled = true,
    DirectoryIdentityState DirectoryState = DirectoryIdentityState.Unknown, DateTimeOffset? CheckedAt = null)
{
    public bool AllowsExecution(DateTimeOffset now) => Enabled && DirectoryState == DirectoryIdentityState.Active &&
        CheckedAt is { } checkedAt && checkedAt <= now.AddSeconds(5) && checkedAt >= now.AddSeconds(-60);
}

public sealed class EimMappingStore(SqliteConnectionFactory factory)
{
    public EimMapping? Get(string profile)
    {
        new ServiceAuthorization(factory).RequireProfileRead(profile);
        return GetInternal(profile);
    }

    internal EimMapping? GetInternal(string profile)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sys_eim_mappings WHERE profile=$profile";
        command.Parameters.AddWithValue("$profile", profile.ToUpperInvariant());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<EimMapping> List()
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM sys_eim_mappings ORDER BY profile";
        using var reader = command.ExecuteReader();
        var results = new List<EimMapping>();
        while (reader.Read()) results.Add(Read(reader));
        return results;
    }

    public void Add(EimMapping mapping)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        mapping = mapping with { Profile = mapping.Profile.ToUpperInvariant() };
        if (Ipc.Services.Events.OperationIdentity.Current?.Principal == mapping.Profile)
            throw new CpfException("CPF9802", "Use another security administrator to change the executing profile's identity mapping.");
        if (!ObjectName.IsValid(mapping.Profile) || new[] { "QSECOFR", "QSECADM", "QSYS", "QSYSOPR", "QUSER", "QPGMR" }.Contains(mapping.Profile))
            throw new CpfException("IPC0003", "EIM requires a non-system profile; retain a local recovery administrator.");
        if (new[] { mapping.Directory, mapping.DistinguishedName, mapping.EntryId, mapping.Principal, mapping.LinuxAccount }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)))
            throw new CpfException("IPC0003", "EIM identity fields are required and cannot contain control characters.");
        if (!mapping.Principal.Contains('@') || mapping.Principal.EndsWith('@') || mapping.Principal.StartsWith('@'))
            throw new CpfException("IPC0003", "An explicit realm-qualified Kerberos principal is required.");
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sys_eim_mappings(profile,directory,distinguished_name,entry_id,principal,linux_account,revision)
            VALUES($profile,$directory,$dn,$entry,$principal,$account,$revision);
            UPDATE sys_profiles SET password_hash=NULL,password_expires=NULL,
                status=CASE WHEN status='PasswordExpired' THEN 'Enabled' ELSE status END WHERE name=$profile;
            """;
        command.Parameters.AddWithValue("$profile", mapping.Profile);
        command.Parameters.AddWithValue("$directory", mapping.Directory);
        command.Parameters.AddWithValue("$dn", mapping.DistinguishedName);
        command.Parameters.AddWithValue("$entry", mapping.EntryId);
        command.Parameters.AddWithValue("$principal", mapping.Principal);
        command.Parameters.AddWithValue("$account", mapping.LinuxAccount);
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
        try { command.ExecuteNonQuery(); transaction.Commit(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new CpfException("IPC0003", "EIM identity conflicts with an existing mapping or the target profile is missing."); }
    }

    public void Remove(string profile)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        if (Ipc.Services.Events.OperationIdentity.Current?.Principal == profile.ToUpperInvariant())
            throw new CpfException("CPF9802", "Use another security administrator to remove the executing profile's identity mapping.");
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sys_profiles SET status='Disabled' WHERE name=$profile AND EXISTS(SELECT 1 FROM sys_eim_mappings WHERE profile=$profile);
            DELETE FROM sys_eim_mappings WHERE profile=$profile;
            """;
        command.Parameters.AddWithValue("$profile", profile.ToUpperInvariant());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal bool RecordProbe(EimMapping snapshot, DirectoryIdentityState state, DateTimeOffset at)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_eim_mappings SET directory_state=$state,checked_at=$at WHERE profile=$profile AND revision=$revision AND (checked_at IS NULL OR checked_at<=$at)";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$at", at.ToString("o"));
        command.Parameters.AddWithValue("$profile", snapshot.Profile);
        command.Parameters.AddWithValue("$revision", snapshot.Revision);
        return command.ExecuteNonQuery() == 1;
    }

    private static EimMapping Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetInt32(7) == 1, Enum.TryParse<DirectoryIdentityState>(reader.GetString(8), out var state) ? state : DirectoryIdentityState.Unknown,
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)));
}
