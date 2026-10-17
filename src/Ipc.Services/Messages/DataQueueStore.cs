using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public enum DataQueueSequence { Fifo, Lifo, Keyed }
public enum DataQueueKeyOrder { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }
public sealed record DataQueueDefinition(string Identity, int MaximumLength, DataQueueSequence Sequence, int KeyLength, bool IncludeSender);
public sealed record DataQueueSender(string User, JobKey? Job);
public sealed record DataQueueEntry(long Id, ProgramBuffer Data, ProgramBuffer Key, DateTimeOffset Sent, DataQueueSender? Sender);
public sealed record DataQueueStatus(DataQueueDefinition Definition, int Entries, long Bytes);

/// <summary>Cross-process atomic queues. Waiting never retains a database transaction.</summary>
public sealed class DataQueueStore(SqliteConnectionFactory factory)
{
    public const int MaximumEntryLength = 64512;
    public const int MaximumEntries = 4096;
    public const int MaximumBytes = 16 * 1024 * 1024;
    private sealed record Payload(int Version, DataQueueDefinition Definition);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly ServiceAuthorization _authorization = new(factory);

    public void Create(QualifiedName queue, int maximumLength, DataQueueSequence sequence = DataQueueSequence.Fifo,
        int keyLength = 0, bool includeSender = false, string text = "", string authority = "*LIBCRTAUT", int ccsid = 37)
    {
        ValidateName(queue);
        queue = new JobTemporaryLibrary(factory).Resolve(queue, ObjectType.DataQueue);
        var definition = new DataQueueDefinition(Guid.NewGuid().ToString("N"), maximumLength, sequence, keyLength, includeSender);
        ValidateDefinition(definition);
        if (!CodePage.IsSupported(ccsid) || text.Length > 50 || text.Any(char.IsControl)) throw Invalid("Invalid data-queue text or CCSID.");
        if (text == "*BLANK") text = "";
        authority = authority.ToUpperInvariant();
        var objects = new SqliteObjectStore(factory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var library = objects.GetRequired("QSYS", queue.Library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var isList = ObjectName.IsValid(authority);
        if (isList)
        {
            _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList);
            _authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        }
        AuthorityBit bits;
        try { bits = isList ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw Invalid("Invalid data-queue public authority."); }
        try
        {
            objects.Create(new ObjectDescriptor { Key = queue, ObjectType = ObjectType.DataQueue, Attribute = "STD", Format = "IPCDQ1",
                Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", Description = text, Ccsid = ccsid,
                PublicAuthority = bits, UseAuthorizationListPublicAuthority = isList,
                Source = JsonSerializer.Serialize(new Payload(1, definition), Json) }, connection, transaction);
        }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode is 1555 or 2067)
        { throw new CpfException("CPF2112", "Data queue already exists."); }
        if (isList)
        {
            using var attach = Command(connection, transaction, queue,
                "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*DTAQ',$list,1,$bits)");
            attach.Parameters.AddWithValue("$list", authority); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public DataQueueStatus Inspect(QualifiedName queue)
    {
        ValidateName(queue); queue = new JobTemporaryLibrary(factory).Resolve(queue, ObjectType.DataQueue); Authorize(queue, Authorities.UseBits);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction();
        var definition = ReadDefinition(connection, transaction, queue, Authorities.UseBits);
        var (entries, bytes) = Size(connection, transaction, queue);
        transaction.Commit(); return new(definition, entries, bytes);
    }

    public long Send(QualifiedName queue, ProgramBuffer data, ProgramBuffer? key = null, CancellationToken cancellationToken = default)
    {
        ValidateName(queue);
        queue = new JobTemporaryLibrary(factory).Resolve(queue, ObjectType.DataQueue);
        if (data.Length is < 1 or > MaximumEntryLength || key?.Length > 256) throw Invalid("Invalid data-queue entry or key length.");
        cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, AuthorityBit.ObjectOperate | AuthorityBit.Add);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        var definition = ReadDefinition(connection, transaction, queue, AuthorityBit.ObjectOperate | AuthorityBit.Add);
        ValidateSelection(definition, key, null);
        if (data.Length > definition.MaximumLength) throw new CpfException("CPF9506", "Entry exceeds the data queue's maximum length.");
        var (entries, bytes) = Size(connection, transaction, queue);
        if (entries >= MaximumEntries || bytes + data.Length + (key?.Length ?? 0) > MaximumBytes)
            throw new CpfException("CPF9504", "Data queue capacity exceeded.");
        var identity = definition.IncludeSender ? OperationIdentity.Current : null;
        using var insert = Command(connection, transaction, queue, """
            INSERT INTO sys_data_queue_entries(queue_lib,queue_name,queue_identity,key_data,data,ccsid,sent,sender,sender_job,sender_job_name,sender_job_user)
            VALUES($lib,$name,$identity,$key,$data,$ccsid,$sent,$sender,$job,$jobname,$jobuser) RETURNING id
            """);
        insert.Parameters.AddWithValue("$identity", definition.Identity);
        insert.Parameters.AddWithValue("$key", key?.ToArray() ?? Array.Empty<byte>()); insert.Parameters.AddWithValue("$data", data.ToArray());
        insert.Parameters.AddWithValue("$ccsid", data.Ccsid); insert.Parameters.AddWithValue("$sent", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$sender", definition.IncludeSender ? identity?.Principal ?? "QSYS" : DBNull.Value);
        insert.Parameters.AddWithValue("$job", (object?)identity?.Job?.Number ?? DBNull.Value);
        insert.Parameters.AddWithValue("$jobname", (object?)identity?.Job?.Name ?? DBNull.Value);
        insert.Parameters.AddWithValue("$jobuser", (object?)identity?.Job?.User ?? DBNull.Value);
        cancellationToken.ThrowIfCancellationRequested();
        var id = (long)insert.ExecuteScalar()!;
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return id;
    }

    public DataQueueEntry? Receive(QualifiedName queue, ProgramBuffer? key = null, DataQueueKeyOrder? keyOrder = null,
        TimeSpan wait = default, bool remove = true, CancellationToken cancellationToken = default,
        int maximumDataLength = MaximumEntryLength, int maximumKeyLength = 256,
        Action<DataQueueEntry>? prepareReceiver = null)
    {
        ValidateName(queue);
        queue = new JobTemporaryLibrary(factory).Resolve(queue, ObjectType.DataQueue);
        if (wait != Timeout.InfiniteTimeSpan && (wait < TimeSpan.Zero || wait > TimeSpan.FromDays(7)) ||
            maximumDataLength is < 1 or > MaximumEntryLength || maximumKeyLength is < 0 or > 256 || key?.Length > 256 ||
            keyOrder is { } order && !Enum.IsDefined(order)) throw Invalid("Invalid data-queue receive options.");
        var required = AuthorityBit.ObjectOperate | AuthorityBit.Read | (remove ? AuthorityBit.Delete : AuthorityBit.None);
        var started = Stopwatch.GetTimestamp(); string? expectedIdentity = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, required);
            using (var connection = factory.Open())
            using (var transaction = BeginWrite(connection, cancellationToken))
            {
                var definition = ReadDefinition(connection, transaction, queue, required);
                if (expectedIdentity is not null && definition.Identity != expectedIdentity)
                    throw new CpfException("CPF9801", "Data queue was replaced while receiving.");
                expectedIdentity ??= definition.Identity;
                ValidateSelection(definition, key, keyOrder);
                var condition = Condition(key, keyOrder);
                // All keyed comparisons search ascending, including LE/LT/NE; duplicate keys are FIFO.
                var sorting = definition.Sequence == DataQueueSequence.Keyed ? "key_data,id" : definition.Sequence == DataQueueSequence.Lifo ? "id DESC" : "id";
                using var select = Command(connection, transaction, queue,
                    "SELECT id,data,key_data,ccsid,sent,sender,sender_job,sender_job_name,sender_job_user,queue_identity FROM sys_data_queue_entries WHERE queue_lib=$lib AND queue_name=$name" + condition + " ORDER BY " + sorting + " LIMIT 1");
                if (key is not null) select.Parameters.AddWithValue("$key", key.ToArray());
                DataQueueEntry? found = null;
                using (var reader = select.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        if (reader.GetString(9) != definition.Identity) throw Invalid("Data-queue entry identity does not match its definition.");
                        var data = (byte[])reader[1]; var returnedKey = (byte[])reader[2]; var ccsid = reader.GetInt32(3);
                        if (data.Length > maximumDataLength || returnedKey.Length > maximumKeyLength)
                            throw new CpfException("CPF9506", "Data-queue receiver is too small; entry was retained.");
                        if (data.Length > definition.MaximumLength || returnedKey.Length != definition.KeyLength) throw Invalid("Data-queue entry layout does not match its definition.");
                        var sender = reader.IsDBNull(5) ? null : new DataQueueSender(reader.GetString(5), reader.IsDBNull(6) ? null : new JobKey(reader.GetInt32(6), reader.GetString(7), reader.GetString(8)));
                        found = new(reader.GetInt64(0), new(data, ccsid), new(returnedKey, ccsid), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), sender);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // API marshalling can reject an entry-dependent output layout
                // before removal. This callback must only prepare memory outputs.
                if (found is not null) prepareReceiver?.Invoke(found);
                if (found is not null && remove)
                {
                    using var delete = Command(connection, transaction, queue, "DELETE FROM sys_data_queue_entries WHERE id=$id AND queue_lib=$lib AND queue_name=$name");
                    delete.Parameters.AddWithValue("$id", found.Id); delete.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
                if (found is not null) return found;
            }
            var remaining = wait == Timeout.InfiniteTimeSpan ? TimeSpan.FromMilliseconds(50) : wait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            cancellationToken.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
        }
    }

    public int Clear(QualifiedName queue, ProgramBuffer? key = null, DataQueueKeyOrder? keyOrder = null, CancellationToken cancellationToken = default)
    {
        ValidateName(queue); queue = new JobTemporaryLibrary(factory).Resolve(queue, ObjectType.DataQueue); cancellationToken.ThrowIfCancellationRequested();
        if (keyOrder is { } order && !Enum.IsDefined(order)) throw Invalid("Invalid key comparison.");
        var required = AuthorityBit.ObjectOperate | AuthorityBit.Delete; Authorize(queue, required);
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        var definition = ReadDefinition(connection, transaction, queue, required);
        if (key is not null || keyOrder is not null) ValidateSelection(definition, key, keyOrder);
        using var clear = Command(connection, transaction, queue, "DELETE FROM sys_data_queue_entries WHERE queue_lib=$lib AND queue_name=$name" + Condition(key, keyOrder));
        if (key is not null) clear.Parameters.AddWithValue("$key", key.ToArray());
        var count = clear.ExecuteNonQuery(); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return count;
    }

    private void Authorize(QualifiedName queue, AuthorityBit bits)
    {
        _authorization.RequireObject(queue.Library, queue.Name.Value, ObjectType.DataQueue, bits);
    }
    private DataQueueDefinition ReadDefinition(SqliteConnection connection, SqliteTransaction transaction, QualifiedName queue, AuthorityBit bits)
    {
        _authorization.RequireObject(queue.Library, queue.Name.Value, ObjectType.DataQueue, bits, acquireLocks: false);
        var descriptor = ObjectSigningService.Read(connection, transaction, queue.Library, queue.Name.Value, ObjectType.DataQueue)
            ?? throw new CpfException("CPF9801", "Data queue not found.");
        try
        {
            if (descriptor.Format != "IPCDQ1" || descriptor.Attribute != "STD" || descriptor.Source is null || descriptor.Source.Length > 2048) throw Invalid("Invalid data-queue definition.");
            using var document = JsonDocument.Parse(descriptor.Source, new JsonDocumentOptions { MaxDepth = 8 });
            CheckObject(document.RootElement);
            var payload = JsonSerializer.Deserialize<Payload>(descriptor.Source, Json);
            if (payload is null || payload.Version != 1 || payload.Definition is null) throw Invalid("Unsupported data-queue definition.");
            ValidateDefinition(payload.Definition); return payload.Definition;
        }
        catch (JsonException) { throw Invalid("Malformed data-queue definition."); }
    }
    private static void CheckObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Invalid("Duplicate data-queue definition property.");
            CheckObject(property.Value);
        }
    }
    private static void ValidateDefinition(DataQueueDefinition definition)
    {
        if (definition.Identity is null || !Guid.TryParseExact(definition.Identity, "N", out _) ||
            definition.MaximumLength is < 1 or > MaximumEntryLength || !Enum.IsDefined(definition.Sequence) ||
            (definition.Sequence == DataQueueSequence.Keyed ? definition.KeyLength is < 1 or > 256 : definition.KeyLength != 0)) throw Invalid("Invalid data-queue dimensions or sequence.");
    }
    private static void ValidateSelection(DataQueueDefinition definition, ProgramBuffer? key, DataQueueKeyOrder? keyOrder)
    {
        if (definition.Sequence == DataQueueSequence.Keyed ? key?.Length != definition.KeyLength : key is not null || keyOrder is not null)
            throw Invalid("Keyed queues require an exact-length key; unkeyed queues cannot use keys.");
    }
    private static string Condition(ProgramBuffer? key, DataQueueKeyOrder? order) => key is null ? "" :
        " AND key_data " + (order switch { null or DataQueueKeyOrder.Equal => "=", DataQueueKeyOrder.NotEqual => "<>",
            DataQueueKeyOrder.Greater => ">", DataQueueKeyOrder.GreaterOrEqual => ">=", DataQueueKeyOrder.Less => "<", DataQueueKeyOrder.LessOrEqual => "<=", _ => throw Invalid("Invalid key order.") }) + " $key";
    private static (int Entries, long Bytes) Size(SqliteConnection connection, SqliteTransaction transaction, QualifiedName queue)
    {
        using var command = Command(connection, transaction, queue, "SELECT count(*),coalesce(sum(length(data)+length(key_data)),0) FROM sys_data_queue_entries WHERE queue_lib=$lib AND queue_name=$name");
        using var reader = command.ExecuteReader(); reader.Read(); return (reader.GetInt32(0), reader.GetInt64(1));
    }
    private static SqliteTransaction BeginWrite(SqliteConnection connection, CancellationToken cancellationToken)
    {
        connection.DefaultTimeout = 1; var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return connection.BeginTransaction(deferred: false); }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            { cancellationToken.WaitHandle.WaitOne(25); }
        }
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, QualifiedName queue, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$lib", queue.Library); command.Parameters.AddWithValue("$name", queue.Name.Value); return command;
    }
    private static void ValidateName(QualifiedName queue)
    {
        if (!ObjectName.IsValid(queue.Library) || !ObjectName.IsValid(queue.Name.Value) || queue.Library != queue.Library.ToUpperInvariant() || queue.Name.Value != queue.Name.Value.ToUpperInvariant()) throw Invalid("A resolved uppercase data-queue name is required.");
    }
    private static CpfException Invalid(string message) => new("IPC0141", message);
}
