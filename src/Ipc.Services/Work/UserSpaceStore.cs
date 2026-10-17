using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed record UserSpaceAttributes(string Identity, int Size, byte InitialValue, bool AutomaticallyExtendible,
    int TransferSize, bool OptimumAlignment, string Domain);

/// <summary>Durable binary storage. Positions are one-based; changes and resizing are atomic.</summary>
public sealed partial class UserSpaceStore(SqliteConnectionFactory factory)
{
    public const int MaximumLength = 16776704;
    public const int MaximumAlignedLength = 16773120;
    public const string Format = "IPCUS1";
    private readonly ServiceAuthorization _authorization = new(factory);
    private readonly JobLockStore _locks = new(factory);

    /// <summary>Called by the exclusive session-server owner before accepting jobs, never by ordinary catalog opens.</summary>
    public int RecoverReplacedObjects()
    {
        _authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.SecurityAdministrator);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT name FROM sys_objects WHERE lib='QRPLOBJ' AND type='*USRSPC' AND json_extract(attrs,'$.\"ipc.userspace.replaced\"')='1' ORDER BY name";
        var names = new List<string>(); using (var reader = query.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        foreach (var name in names)
        {
            _locks.RequireNoPersistentAllocation(new("QRPLOBJ", name, ObjectType.UserSpace));
            ObjectCatalogOperations.DeleteCore(new("QRPLOBJ", name), ObjectType.UserSpace, connection, transaction);
        }
        transaction.Commit(); return names.Count;
    }

    public void Create(QualifiedName space, int size, byte initialValue = 0, string attribute = "", string text = "",
        string authority = "*CHANGE", bool replace = false, string domain = "*DEFAULT", int transferSize = 0,
        bool optimumAlignment = true, bool automaticallyExtendible = false, int ccsid = 37, CancellationToken cancellationToken = default)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        ValidateSize(size, optimumAlignment); ValidateTransfer(transferSize);
        attribute = attribute.Trim().ToUpperInvariant(); domain = domain.ToUpperInvariant();
        if (attribute.Length > 0 && !ObjectName.IsValid(attribute) || text.Length > 50 || text.Any(char.IsControl) || !CodePage.IsSupported(ccsid))
            throw Invalid("Invalid user-space attribute, text or CCSID.");
        if (domain is not ("*DEFAULT" or "*USER" or "*SYSTEM")) throw Invalid("Invalid user-space domain.");
        cancellationToken.ThrowIfCancellationRequested();
        _authorization.RequireObject("QSYS", space.Library, ObjectType.Library, AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Add, checkLibrary: false);
        _locks.RequireObjectMutation(space.Library, space.Name.Value, ObjectType.UserSpace);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        var objects = new SqliteObjectStore(factory);
        var library = objects.GetRequired("QSYS", space.Library, ObjectType.Library);
        var allowUser = UserDomainAllowed(connection, transaction, space.Library);
        if (domain == "*USER" && !allowUser) throw new CpfException("CPF3C49", "The library does not allow user-domain objects.");
        if (domain == "*DEFAULT") domain = allowUser ? "*USER" : "*SYSTEM";
        ObjectDescriptor? previous = null;
        using (var exists = Command(connection, transaction, space, "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*USRSPC'"))
            if ((long)exists.ExecuteScalar()! != 0) previous = ObjectSigningService.Read(connection, transaction, space.Library, space.Name.Value, ObjectType.UserSpace);
        if (previous is not null && !replace) throw new CpfException("CPF9870", "User space already exists.");

        string? list = null;
        var descriptor = new ObjectDescriptor { Key = space, ObjectType = ObjectType.UserSpace, Attribute = attribute,
            Format = Format, Source = Guid.NewGuid().ToString("N"), Description = text == "*BLANK" ? "" : text, Ccsid = ccsid,
            Owner = OperationIdentity.Current?.Principal ?? "QSECOFR" };
        QualifiedName? archive = null;
        if (previous is not null)
        {
            _authorization.RequireObject(space.Library, space.Name.Value, ObjectType.UserSpace, AuthorityBit.ObjectManagement | AuthorityBit.ObjectExist | AuthorityBit.Read, acquireLocks: false);
            _ = ReadAttributes(connection, transaction, space);
            // Preserve object/private/AUTL authorities even when the supplied AUT parameter is invalid.
            descriptor.Owner = previous.Owner; descriptor.PublicAuthority = previous.PublicAuthority;
            descriptor.UseAuthorizationListPublicAuthority = previous.UseAuthorizationListPublicAuthority;
            archive = ArchiveName(connection, transaction);
            _locks.RequireObjectMutation(archive.Value.Library, archive.Value.Name.Value, ObjectType.UserSpace);
            ObjectCatalogOperations.RelocateOne(connection, transaction, space, ObjectType.UserSpace, archive.Value, false, allowRestrictedUserSpaceArchive: true);
            using var describe = Command(connection, transaction, archive.Value, "UPDATE sys_objects SET description=$text,attrs=json_set(coalesce(attrs,'{}'),'$.\"ipc.userspace.replaced\"','1') WHERE lib=$lib AND name=$name AND type='*USRSPC'");
            describe.Parameters.AddWithValue("$text", space.Name.Value); describe.ExecuteNonQuery();
        }
        else
        {
            authority = authority.Trim().ToUpperInvariant();
            if (authority == "*LIBCRTAUT") authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
            if (ObjectName.IsValid(authority))
            {
                _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList);
                _authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
                list = authority; descriptor.UseAuthorizationListPublicAuthority = true; descriptor.PublicAuthority = AuthorityBit.None;
            }
            else
            {
                try { descriptor.PublicAuthority = Authorities.FromLevel(authority); }
                catch (ArgumentException) { throw Invalid("Invalid user-space public authority."); }
            }
            _authorization.RequireCreate(descriptor);
        }
        objects.CreateAuthorized(descriptor, connection, transaction);
        using (var insert = Command(connection, transaction, space, """
            INSERT INTO sys_user_spaces(space_lib,space_name,identity,initial_byte,auto_extend,transfer_size,aligned,domain,data)
            VALUES($lib,$name,$identity,$initial,$extend,$transfer,$aligned,$domain,$data)
            """))
        {
            insert.Parameters.AddWithValue("$identity", descriptor.Source!); insert.Parameters.AddWithValue("$initial", (int)initialValue);
            insert.Parameters.AddWithValue("$extend", automaticallyExtendible ? 1 : 0); insert.Parameters.AddWithValue("$transfer", transferSize);
            insert.Parameters.AddWithValue("$aligned", optimumAlignment ? 1 : 0); insert.Parameters.AddWithValue("$domain", domain);
            insert.Parameters.AddWithValue("$data", Enumerable.Repeat(initialValue, size).ToArray()); insert.ExecuteNonQuery();
        }
        if (archive is { } old)
        {
            using var copyAuthority = Command(connection, transaction, space, "INSERT INTO sys_authorities SELECT $lib,$name,type,holder,is_authl,bits FROM sys_authorities WHERE lib=$oldlib AND name=$oldname AND type='*USRSPC'");
            copyAuthority.Parameters.AddWithValue("$oldlib", old.Library); copyAuthority.Parameters.AddWithValue("$oldname", old.Name.Value); copyAuthority.ExecuteNonQuery();
            if (previous is not null && ReadAttributes(connection, transaction, old).Domain == "*USER" && !UserDomainAllowed(connection, transaction, old.Library))
                ObjectCatalogOperations.DeleteCore(old, ObjectType.UserSpace, connection, transaction);
        }
        else if (list is not null)
        {
            using var attach = Command(connection, transaction, space, "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*USRSPC',$list,1,$bits)");
            attach.Parameters.AddWithValue("$list", list); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public UserSpaceAttributes Inspect(QualifiedName space, CancellationToken cancellationToken = default)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        cancellationToken.ThrowIfCancellationRequested(); Authorize(space, Authorities.UseBits, false);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        Authorize(space, Authorities.UseBits, false, false); var found = ReadAttributes(connection, transaction, space);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return found;
    }

    public byte[] Read(QualifiedName space, int start, int length, CancellationToken cancellationToken = default)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        ValidateRangeInput(start, length); cancellationToken.ThrowIfCancellationRequested(); Authorize(space, Authorities.UseBits, false);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        Authorize(space, Authorities.UseBits, false, false); var found = ReadAttributes(connection, transaction, space); Range(found.Size, start, length);
        using var query = Command(connection, transaction, space, "SELECT substr(data,$start,$length) FROM sys_user_spaces WHERE space_lib=$lib AND space_name=$name");
        query.Parameters.AddWithValue("$start", start); query.Parameters.AddWithValue("$length", length);
        var bytes = (byte[])query.ExecuteScalar()!; cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return bytes;
    }

    public void Write(QualifiedName space, int start, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default, bool forceToStorage = false)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        ValidateRangeInput(start, data.Length); cancellationToken.ThrowIfCancellationRequested();
        Authorize(space, Authorities.ChangeBits, true, false);
        _locks.RequireExclusiveRead(space.Library, space.Name.Value, ObjectType.UserSpace);
        using var connection = factory.Open();
        if (forceToStorage) { using var durability = connection.CreateCommand(); durability.CommandText = "PRAGMA synchronous=FULL"; durability.ExecuteNonQuery(); }
        using var transaction = BeginWrite(connection, cancellationToken);
        Authorize(space, Authorities.ChangeBits, true, false); var found = ReadAttributes(connection, transaction, space); Range(found.Size, start, data.Length);
        // SQLite substr on BLOB uses byte offsets. CAST preserves binary concatenation, including NUL and invalid UTF-8.
        using var update = Command(connection, transaction, space, "UPDATE sys_user_spaces SET data=CAST(substr(data,1,$prefix)||$data||substr(data,$suffix) AS BLOB) WHERE space_lib=$lib AND space_name=$name");
        update.Parameters.AddWithValue("$prefix", start - 1); update.Parameters.AddWithValue("$suffix", (long)start + data.Length);
        update.Parameters.AddWithValue("$data", data.ToArray()); update.ExecuteNonQuery(); Changed(connection, transaction, space);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    public void ChangeAttributes(QualifiedName space, int? size = null, byte? initialValue = null, bool? automaticallyExtendible = null,
        int? transferSize = null, CancellationToken cancellationToken = default)
    {
        ValidateName(space); space = new JobTemporaryLibrary(factory).Resolve(space, ObjectType.UserSpace);
        if (transferSize is { } request) ValidateTransfer(request);
        cancellationToken.ThrowIfCancellationRequested(); var required = Authorities.ChangeBits | AuthorityBit.ObjectManagement;
        Authorize(space, required, true);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        Authorize(space, required, true, false); var found = ReadAttributes(connection, transaction, space);
        var newSize = size ?? found.Size; ValidateSize(newSize, found.OptimumAlignment);
        using var read = Command(connection, transaction, space, "SELECT data FROM sys_user_spaces WHERE space_lib=$lib AND space_name=$name");
        var bytes = (byte[])read.ExecuteScalar()!;
        if (newSize != bytes.Length)
        {
            var resized = Enumerable.Repeat(initialValue ?? found.InitialValue, newSize).ToArray();
            bytes.AsSpan(0, Math.Min(bytes.Length, newSize)).CopyTo(resized); bytes = resized;
        }
        using var update = Command(connection, transaction, space, "UPDATE sys_user_spaces SET data=$data,initial_byte=$initial,auto_extend=$extend,transfer_size=$transfer WHERE space_lib=$lib AND space_name=$name");
        update.Parameters.AddWithValue("$data", bytes); update.Parameters.AddWithValue("$initial", (int)(initialValue ?? found.InitialValue));
        update.Parameters.AddWithValue("$extend", (automaticallyExtendible ?? found.AutomaticallyExtendible) ? 1 : 0);
        update.Parameters.AddWithValue("$transfer", transferSize ?? found.TransferSize); update.ExecuteNonQuery(); Changed(connection, transaction, space);
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }

    private void Authorize(QualifiedName space, AuthorityBit required, bool mutation, bool acquireLocks = true)
    {
        _authorization.RequireObject(space.Library, space.Name.Value, ObjectType.UserSpace, required, acquireLocks: acquireLocks);
        if (acquireLocks)
        {
            if (mutation) _locks.RequireObjectMutation(space.Library, space.Name.Value, ObjectType.UserSpace);
            else _locks.RequireStableRead(space.Library, space.Name.Value, ObjectType.UserSpace);
        }
    }
    internal static UserSpaceAttributes ReadAttributes(SqliteConnection connection, SqliteTransaction transaction, QualifiedName space)
    {
        var descriptor = ObjectSigningService.Read(connection, transaction, space.Library, space.Name.Value, ObjectType.UserSpace);
        using var query = Command(connection, transaction, space, "SELECT identity,length(data),initial_byte,auto_extend,transfer_size,aligned,domain FROM sys_user_spaces WHERE space_lib=$lib AND space_name=$name");
        using var reader = query.ExecuteReader();
        if (!reader.Read() || descriptor.Format != Format || descriptor.Source != reader.GetString(0) || !Guid.TryParseExact(descriptor.Source, "N", out _))
            throw new CpfException("IPC0142", "Invalid user-space object/payload binding.");
        return new(reader.GetString(0), reader.GetInt32(1), (byte)reader.GetInt32(2), reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5) != 0, reader.GetString(6));
    }
    internal static bool UserDomainAllowed(SqliteConnection connection, SqliteTransaction transaction, string library)
    {
        library = JobTemporaryLibrary.LogicalLibrary(connection, transaction, library);
        using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = "SELECT value FROM sys_sysvals WHERE name='QALWUSRDMN'";
        var value = query.ExecuteScalar() as string ?? "*ALL";
        var entries = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return entries.Contains("*ALL", StringComparer.OrdinalIgnoreCase) || entries.Contains(library, StringComparer.OrdinalIgnoreCase) ||
            library == LibraryNames.QTemp && entries.Contains("*DIR", StringComparer.OrdinalIgnoreCase);
    }
    private static QualifiedName ArchiveName(SqliteConnection connection, SqliteTransaction transaction)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var name = new QualifiedName("QRPLOBJ", "U" + DateTime.UtcNow.ToString("MMdd", System.Globalization.CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());
            using var query = Command(connection, transaction, name, "SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*USRSPC'");
            if ((long)query.ExecuteScalar()! == 0) return name;
        }
        throw new CpfException("CPF3C01", "Could not allocate a replacement user-space name.");
    }
    private static void Changed(SqliteConnection connection, SqliteTransaction transaction, QualifiedName space)
    {
        using var update = Command(connection, transaction, space, "UPDATE sys_objects SET changed=$at,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type='*USRSPC'");
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); update.ExecuteNonQuery();
    }
    private static void ValidateName(QualifiedName space)
    {
        if (!ObjectName.IsValid(space.Library) || !ObjectName.IsValid(space.Name.Value) || space.Library != space.Library.ToUpperInvariant() || space.Name.Value != space.Name.Value.ToUpperInvariant())
            throw Invalid("A resolved uppercase user-space name is required.");
    }
    private static void ValidateSize(int size, bool aligned)
    { if (size < 1 || size > (aligned ? MaximumAlignedLength : MaximumLength)) throw Invalid("Invalid user-space size."); }
    private static void ValidateTransfer(int transfer)
    { if (transfer is < 0 or > 32) throw Invalid("Transfer-size request must be 0 through 32."); }
    private static void ValidateRangeInput(int start, int length)
    {
        if (start < 1) throw new CpfException("CPF3C0F", "Starting position must be positive.");
        if (length < 1) throw new CpfException("CPF3C12", "User-space length must be positive.");
    }
    private static void Range(int size, int start, int length)
    { if (start > size || length > size - start + 1) throw new CpfException("CPF3C14", "User-space range exceeds its size."); }
    private static SqliteTransaction BeginWrite(SqliteConnection connection, CancellationToken cancellationToken)
    {
        connection.DefaultTimeout = 1; var began = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return connection.BeginTransaction(deferred: false); }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(30))
            { cancellationToken.WaitHandle.WaitOne(25); }
        }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, QualifiedName space, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$lib", space.Library); command.Parameters.AddWithValue("$name", space.Name.Value); return command;
    }
    private static CpfException Invalid(string message) => new("CPF3C4B", message);
}
