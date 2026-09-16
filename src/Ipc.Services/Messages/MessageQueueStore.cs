using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public enum MessageSelection { Next, First, Last, Key, Reply, After, Before, ExceptionNewest }
public sealed record QueuedMessage(uint Key, string Kind, ProgramBuffer Data, string MessageId, int Severity,
    string Sender, int? SenderJob, DateTimeOffset Sent, bool Seen, bool Replied, uint? CorrelationKey,
    bool ExceptionHandled = false, string ReplyTypeCode = "21", MessageOrigin? Origin = null, ProgramBuffer? SenderInformation = null,
    PredefinedMessage? Predefined = null, ProgramBuffer? SecondLevel = null, ProgramBuffer? ReplacementData = null, string? ActualMessageFileLibrary = null)
{
    public string? ReturnType => Kind switch {
        "COMP" => "01", "DIAG" => "02", "INFO" => "04", "INQ" => "05", "COPY" => "06",
        "NOTIFY" => ExceptionHandled ? "14" : "16", "ESCAPE" => ExceptionHandled ? "15" : "17",
        "RPY" => ReplyTypeCode, _ => null };
    public ProgramBuffer KeyBuffer(int ccsid)
    { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, Key); return new(bytes, ccsid); }
}

/// <summary>Durable, bounded named queues. A receive never holds a database transaction while waiting.</summary>
public sealed partial class MessageQueueStore(SqliteConnectionFactory factory)
{
    public const int MaximumMessages = 4096;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private const AuthorityBit SendAuthority = AuthorityBit.ObjectOperate | AuthorityBit.Add;
    private sealed record Stored(QueuedMessage Message, MessageQueueAddress? ReplyQueue, ProgramBuffer DefaultReply, string DefaultReplyCode);

    public void Create(QualifiedName queue, string text = "", string authority = "*LIBCRTAUT")
    {
        ValidateQueue(queue); authority = authority.ToUpperInvariant();
        if (text == "*BLANK") text = "";
        if (text.Length > 50 || text.Any(char.IsControl)) throw Invalid("Queue description allows 50 printable characters.");
        var objects = new SqliteObjectStore(factory); var authorization = new ServiceAuthorization(factory);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var library = objects.GetRequired("QSYS", queue.Library, ObjectType.Library);
        if (authority == "*LIBCRTAUT") authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
        var isList = ObjectName.IsValid(authority);
        if (isList) authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
        if (isList) _ = objects.GetRequired("QSYS", authority, ObjectType.AuthorizationList);
        AuthorityBit bits;
        try { bits = isList ? AuthorityBit.None : Authorities.FromLevel(authority); }
        catch (ArgumentException) { throw Invalid("Invalid queue authority."); }
        try
        {
            objects.Create(new() { Key = queue, ObjectType = ObjectType.MessageQueue, Description = text,
                Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", PublicAuthority = bits,
                UseAuthorizationListPublicAuthority = isList }, connection, transaction);
        }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode is 1555 or 2067)
        { throw new CpfException("CPF2112", "Message queue already exists."); }
        if (isList)
        {
            using var attach = Command(connection, transaction, new MessageQueueAddress(queue), "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*MSGQ',$list,1,$bits)");
            attach.Parameters.AddWithValue("$list", authority); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public IReadOnlyList<uint> SendTo(IReadOnlyList<MessageQueueAddress> queues, ProgramBuffer data, string kind = "INFO",
        MessageQueueAddress? replyQueue = null, ProgramBuffer? defaultReply = null, string messageId = "", int severity = 0,
        CancellationToken cancellationToken = default, bool senderCopy = false, PredefinedMessage? predefined = null)
    {
        if (queues.Count is < 1 or > 50 || queues.Distinct().Count() != queues.Count) throw Invalid("Specify one to fifty distinct queues.");
        if (kind is not ("INFO" or "INQ" or "COMP" or "DIAG" or "STATUS" or "ESCAPE" or "NOTIFY")) throw Invalid("Invalid message type.");
        if (data.Length > 4096 || severity is < 0 or > 99 || messageId.Length != 0 && (messageId.Length != 7 || messageId.Any(c => !char.IsAsciiLetterOrDigit(c)))) throw Invalid("Invalid message payload, ID or severity.");
        if (kind == "INQ" && (queues.Count != 1 || replyQueue is null) || kind != "INQ" && (replyQueue is not null || defaultReply is not null)) throw Invalid("An inquiry requires exactly one destination and a reply queue.");
        if (senderCopy && kind != "INQ") throw Invalid("Sender copies require an inquiry.");
        if (predefined is not null)
        {
            predefined = PredefinedMessage.Restore(predefined.Serialize())!;
            var formatted = MessageDescriptionFormat.Format(predefined.Description, predefined.Replacement, data.Ccsid);
            if (messageId.Length == 0 || formatted.Severity != severity || !formatted.Text.ToArray().AsSpan().SequenceEqual(data.ToArray()))
                throw Invalid("Message text and predefined snapshot do not agree.");
            _ = new SqliteObjectStore(factory).GetRequired(predefined.Library, predefined.File, ObjectType.MessageFile);
        }
        var defaultReplyCode = defaultReply is null ? "24" : "23";
        defaultReply ??= new ProgramBuffer(Array.Empty<byte>(), data.Ccsid);
        if (defaultReply.Length > 132 || defaultReply.Ccsid != data.Ccsid) throw Invalid("Invalid default reply length or CCSID.");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var queue in queues) Authorize(queue, SendAuthority);
        if (replyQueue is { } reply) Authorize(reply, SendAuthority);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var keys = new List<uint>();
        foreach (var queue in queues)
        {
            cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, SendAuthority);
            if (replyQueue is { } target) Authorize(target, SendAuthority);
            if (predefined is not null) _ = new SqliteObjectStore(factory).GetRequired(predefined.Library, predefined.File, ObjectType.MessageFile);
            var key = Insert(connection, transaction, queue, data, kind, messageId, severity, replyQueue, defaultReply, null, defaultReplyCode: defaultReplyCode, predefined: predefined);
            keys.Add(senderCopy ? Insert(connection, transaction, replyQueue!, data, "COPY", messageId, severity, null,
                new ProgramBuffer(Array.Empty<byte>(), data.Ccsid), key, predefined: predefined) : key);
        }
        transaction.Commit(); return keys;
    }
    public QueuedMessage? ReceiveFrom(MessageQueueAddress queue, MessageSelection selection = MessageSelection.Next, uint key = 0,
        bool remove = true, TimeSpan wait = default, CancellationToken cancellationToken = default, string? kind = null, int? receiveCcsid = null,
        bool requireReturnType = false, bool keepException = false, int senderLength = 0, bool longSender = false, int senderCcsid = 37)
    {
        if (!Enum.IsDefined(selection) || selection is MessageSelection.Key or MessageSelection.Reply && key == 0) throw Invalid("A nonzero message key is required.");
        if (wait != Timeout.InfiniteTimeSpan && (wait < TimeSpan.Zero || wait > TimeSpan.FromDays(7))) throw Invalid("Invalid receive wait.");
        if (kind is not (null or "INFO" or "INQ" or "RPY" or "COMP" or "DIAG" or "STATUS" or "ESCAPE" or "NOTIFY" or "COPY")) throw Invalid("Invalid receive message type.");
        if (remove && keepException) throw Invalid("Keeping exceptions requires a non-removing receive.");
        if (senderLength != 0 && (senderLength < (longSender ? 720 : 80) || senderLength > 32767 || !Ipc.Core.Text.CodePage.IsSupported(senderCcsid)))
            throw Invalid("Invalid sender return layout.");
        if (receiveCcsid is { } targetCcsid && !Ipc.Core.Text.CodePage.IsSupported(targetCcsid)) throw Invalid("Unsupported receive CCSID.");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var required = Authorities.UseBits | (remove ? AuthorityBit.Delete : AuthorityBit.None);
            Authorize(queue, required);
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                cancellationToken.ThrowIfCancellationRequested(); Authorize(queue, required);
                var found = Find(connection, transaction, queue, selection, key, kind);
                if (found is not null)
                {
                    var received = found.Message;
                    if (requireReturnType && received.ReturnType is null) throw Invalid("This message kind has no supported return type.");
                    if (senderLength != 0) received = received with { SenderInformation = MessageSenderLayout.Encode(received, senderLength, longSender, senderCcsid) };
                    if (received.Predefined is { } definition)
                    {
                        var outputCcsid = receiveCcsid ?? received.Data.Ccsid;
                        var formatted = MessageDescriptionFormat.Format(definition.Description, definition.Replacement, outputCcsid);
                        var replacement = receiveCcsid is null ? definition.Replacement : MessageDescriptionFormat.ConvertReplacementData(definition.Description, definition.Replacement, outputCcsid);
                        using var file = connection.CreateCommand(); file.Transaction = transaction;
                        file.CommandText = "SELECT created FROM sys_objects WHERE lib=$filelib AND name=$filename AND type='*MSGF'";
                        file.Parameters.AddWithValue("$filelib", definition.Library); file.Parameters.AddWithValue("$filename", definition.File);
                        var actualLibrary = file.ExecuteScalar() is string created && DateTimeOffset.Parse(created, CultureInfo.InvariantCulture) == definition.FileCreated ? definition.Library : "";
                        received = received with { Data = formatted.Text, SecondLevel = formatted.SecondLevel, ReplacementData = replacement, ActualMessageFileLibrary = actualLibrary };
                    }
                    else if (receiveCcsid is { } ccsid && ccsid != received.Data.Ccsid)
                    {
                        var encoding = (System.Text.Encoding)Ipc.Core.Text.CodePage.FromCcsid(ccsid).Clone();
                        encoding.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback;
                        try { received = received with { Data = new ProgramBuffer(encoding.GetBytes(received.Data.ToText()), ccsid) }; }
                        catch (System.Text.EncoderFallbackException) { throw new CpfException("CPF2479", "Message cannot be converted to the receiving CCSID."); }
                    }
                    if (remove && found.Message.Kind == "INQ" && !found.Message.Replied)
                        ReplyCore(connection, transaction, queue, found, found.DefaultReply, found.DefaultReplyCode);
                    if (remove) DeleteMessage(connection, transaction, queue, found.Message);
                    else if (!(keepException && received.Kind is "ESCAPE" or "NOTIFY" && !received.ExceptionHandled))
                    {
                        using var change = Command(connection, transaction, queue,
                            "UPDATE sys_message_entries SET seen=1,exception_handled=CASE WHEN kind IN ('ESCAPE','NOTIFY') THEN 1 ELSE exception_handled END WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key=$key");
                        change.Parameters.AddWithValue("$key", (long)found.Message.Key); change.ExecuteNonQuery();
                    }
                    transaction.Commit(); return received;
                }
                transaction.Commit();
            }
            var remaining = wait == Timeout.InfiniteTimeSpan ? TimeSpan.FromMilliseconds(100) : wait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            cancellationToken.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
        }
    }
    public uint ReplyTo(MessageQueueAddress queue, uint key, ProgramBuffer? data = null, bool remove = true,
        CancellationToken cancellationToken = default)
    {
        if (key == 0 || data?.Length > 132) throw Invalid("A reply requires a key and at most 132 bytes.");
        cancellationToken.ThrowIfCancellationRequested();
        var required = Authorities.UseBits | AuthorityBit.Add | (remove ? AuthorityBit.Delete : AuthorityBit.None);
        Authorize(queue, required);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        Authorize(queue, required); cancellationToken.ThrowIfCancellationRequested();
        var found = Find(connection, transaction, queue, MessageSelection.Key, key) ?? throw new CpfException("CPF2410", "Message key was not found.");
        var reply = ReplyCore(connection, transaction, queue, found, data ?? found.DefaultReply, data is null ? found.DefaultReplyCode : "21");
        if (remove)
        {
            using var delete = Command(connection, transaction, queue, "DELETE FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key=$key");
            delete.Parameters.AddWithValue("$key", (long)key); delete.ExecuteNonQuery();
        }
        transaction.Commit(); return reply;
    }
    public IReadOnlyList<QueuedMessage> ListFrom(MessageQueueAddress queue, uint after = 0, int limit = 100)
    {
        if (limit is < 1 or > 1000) throw Invalid("Message page size must be 1–1000.");
        Authorize(queue, Authorities.UseBits);
        using var connection = factory.Open(); using var command = Command(connection, null, queue,
            "SELECT * FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key>$after ORDER BY key LIMIT $limit");
        command.Parameters.AddWithValue("$after", (long)after); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<QueuedMessage>();
        while (reader.Read()) result.Add(Read(reader).Message); return result;
    }
    private uint ReplyCore(SqliteConnection connection, SqliteTransaction transaction, MessageQueueAddress queue, Stored found, ProgramBuffer data, string replyTypeCode)
    {
        if (found.Message.Kind != "INQ" || found.Message.Replied) throw new CpfException("CPF2422", "Message is not an unanswered inquiry.");
        var destination = found.ReplyQueue ?? throw new CpfException("CPF2469", "Reply queue no longer exists.");
        Authorize(destination, SendAuthority, replyDelivery: true);
        using var copy = Command(connection, transaction, destination,
            "SELECT key FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND kind='COPY' AND correlation_key=$original LIMIT 1");
        copy.Parameters.AddWithValue("$original", (long)found.Message.Key);
        var copyKey = copy.ExecuteScalar() is long number ? checked((uint)number) : (uint?)null;
        var key = Insert(connection, transaction, destination, data, "RPY", "", 0, null, new ProgramBuffer(Array.Empty<byte>(), data.Ccsid), copyKey ?? found.Message.Key, replyTypeCode: replyTypeCode);
        if (copyKey is not null)
        {
            copy.CommandText = "UPDATE sys_message_entries SET replied=1 WHERE key=$copy";
            copy.Parameters.AddWithValue("$copy", (long)copyKey.Value); copy.ExecuteNonQuery();
        }
        using var mark = Command(connection, transaction, queue, "UPDATE sys_message_entries SET replied=1 WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key=$key");
        mark.Parameters.AddWithValue("$key", (long)found.Message.Key); mark.ExecuteNonQuery(); return key;
    }
    private static uint Insert(SqliteConnection connection, SqliteTransaction transaction, MessageQueueAddress queue, ProgramBuffer data,
        string kind, string messageId, int severity, MessageQueueAddress? replyQueue, ProgramBuffer defaultReply, uint? correlation,
        string replyTypeCode = "21", string defaultReplyCode = "24", QueuedMessage? origin = null, PredefinedMessage? predefined = null)
    {
        var detail = (predefined ?? origin?.Predefined)?.Serialize() ?? "";
        using var command = Command(connection, transaction, queue,
            "SELECT count(*),coalesce(sum(length(data)+length(default_reply)+length(CAST(predefined AS BLOB))),0) FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program");
        if (queue.ProgramQueue is not null)
            command.CommandText = "SELECT count(*),coalesce(sum(length(data)+length(default_reply)+length(CAST(predefined AS BLOB))),0) FROM sys_message_entries WHERE program_queue IN (SELECT id FROM sys_program_message_queues WHERE job_number=(SELECT job_number FROM sys_program_message_queues WHERE id=$program))";
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt64(0) >= MaximumMessages || reader.GetInt64(1) + data.Length + defaultReply.Length + System.Text.Encoding.UTF8.GetByteCount(detail) > MaximumBytes)
                throw new CpfException("CPF2460", "Message queue capacity exceeded.");
        }
        var identity = OperationIdentity.Current; var sent = DateTimeOffset.UtcNow;
        command.CommandText = "SELECT program FROM sys_program_message_queues WHERE id=$program AND external=0";
        var recipient = command.ExecuteScalar() as string ?? "";
        command.CommandText = """
            INSERT INTO sys_message_entries(queue_lib,queue_name,queue_type,program_queue,kind,message_id,severity,data,ccsid,sender,sender_job,sent,reply_lib,reply_name,reply_type,reply_program_queue,default_reply,correlation_key,reply_type_code,default_reply_code,sender_job_name,sender_job_user,sender_program,recipient_program,origin_sent,predefined)
            VALUES($lib,$name,$qtype,$program,$kind,$id,$severity,$data,$ccsid,$sender,$job,$sent,$rlib,$rname,$rtype,$rprogram,$default,$correlation,$replycode,$defaultcode,$jobname,$jobuser,$senderprogram,$recipient,$originsent,$predefined) RETURNING key
            """;
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", messageId); command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$data", data.ToArray()); command.Parameters.AddWithValue("$ccsid", data.Ccsid);
        command.Parameters.AddWithValue("$predefined", detail);
        command.Parameters.AddWithValue("$sender", origin?.Sender ?? identity?.Principal ?? "QSYS");
        command.Parameters.AddWithValue("$job", (object?)(origin is null ? identity?.Job?.Number : origin.SenderJob) ?? DBNull.Value);
        command.Parameters.AddWithValue("$sent", sent.ToString("O"));
        command.Parameters.AddWithValue("$jobname", origin is null ? identity?.Job?.Name ?? "" : origin.Origin?.JobName ?? "");
        command.Parameters.AddWithValue("$jobuser", origin is null ? identity?.Job?.User ?? "" : origin.Origin?.JobUser ?? "");
        command.Parameters.AddWithValue("$senderprogram", origin is null ? identity?.CallStack.LastOrDefault()?.Program.Name.Value ?? "" : origin.Origin?.Program ?? "");
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$originsent", (origin?.Origin?.Sent ?? origin?.Sent ?? sent).ToString("O"));
        command.Parameters.AddWithValue("$rlib", (object?)replyQueue?.Named?.Library ?? DBNull.Value);
        command.Parameters.AddWithValue("$rname", (object?)replyQueue?.Named?.Name.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$rtype", replyQueue?.Named is null ? DBNull.Value : "*MSGQ");
        command.Parameters.AddWithValue("$rprogram", (object?)replyQueue?.ProgramQueue ?? DBNull.Value);
        command.Parameters.AddWithValue("$qtype", queue.Named is null ? DBNull.Value : "*MSGQ");
        command.Parameters.AddWithValue("$default", defaultReply.ToArray()); command.Parameters.AddWithValue("$correlation", correlation is { } id ? (long)id : DBNull.Value);
        command.Parameters.AddWithValue("$replycode", replyTypeCode); command.Parameters.AddWithValue("$defaultcode", defaultReplyCode);
        try { return checked((uint)(long)command.ExecuteScalar()!); }
        catch (SqliteException error) when (error.SqliteExtendedErrorCode == 275) { throw new CpfException("CPF2460", "Message key space exhausted."); }
    }
    private static Stored? Find(SqliteConnection connection, SqliteTransaction transaction, MessageQueueAddress queue, MessageSelection selection, uint key, string? kind = null)
    {
        if (selection == MessageSelection.Key && kind != "COPY")
        {
            using var copy = Command(connection, transaction, queue, "SELECT kind FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program AND key=$key");
            copy.Parameters.AddWithValue("$key", (long)key);
            if (copy.ExecuteScalar() as string == "COPY") selection = MessageSelection.Reply;
        }
        var condition = selection switch { MessageSelection.Next => "AND seen=0", MessageSelection.Key => "AND key=$key", MessageSelection.Reply => "AND kind='RPY' AND correlation_key=$key AND seen=0", MessageSelection.After => "AND key>$key", MessageSelection.Before => "AND key<$key", MessageSelection.ExceptionNewest => "AND kind IN ('ESCAPE','NOTIFY') AND seen=0", _ => "" };
        if (selection == MessageSelection.Next && kind is null) condition += " AND kind<>'COPY'";
        using var command = Command(connection, transaction, queue, "SELECT * FROM sys_message_entries WHERE queue_lib IS $lib AND queue_name IS $name AND program_queue IS $program " + condition + (kind is null ? "" : " AND kind=$kind") + " ORDER BY key " + (selection is MessageSelection.Last or MessageSelection.Before or MessageSelection.ExceptionNewest ? "DESC" : "ASC") + " LIMIT 1");
        if (kind is not null) command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", (long)key); using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null;
    }
    private static Stored Read(SqliteDataReader reader)
    {
        var ccsid = reader.GetInt32(reader.GetOrdinal("ccsid"));
        string Text(string column) => reader.GetString(reader.GetOrdinal(column));
        int? Number(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetInt32(reader.GetOrdinal(column));
        var correlation = reader.IsDBNull(reader.GetOrdinal("correlation_key")) ? (uint?)null : checked((uint)reader.GetInt64(reader.GetOrdinal("correlation_key")));
        var message = new QueuedMessage(checked((uint)reader.GetInt64(0)), Text("kind"), new((byte[])reader["data"], ccsid), Text("message_id"), Number("severity")!.Value,
            Text("sender"), Number("sender_job"), DateTimeOffset.Parse(Text("sent"), CultureInfo.InvariantCulture), Number("seen") == 1, Number("replied") == 1, correlation,
            Number("exception_handled") == 1, Text("reply_type_code"),
            new MessageOrigin(Text("sender_job_name"), Text("sender_job_user"), Text("sender_program"), Text("recipient_program"),
                DateTimeOffset.Parse(Text("origin_sent") is { Length: > 0 } timestamp ? timestamp : Text("sent"), CultureInfo.InvariantCulture)),
            Predefined: PredefinedMessage.Restore(Text("predefined")));
        return new(message, reader.IsDBNull(reader.GetOrdinal("reply_program_queue")) ? reader.IsDBNull(reader.GetOrdinal("reply_lib")) ? null : new MessageQueueAddress(new QualifiedName(Text("reply_lib"), Text("reply_name"))) : new MessageQueueAddress(Text("reply_program_queue")), new((byte[])reader["default_reply"], ccsid), Text("default_reply_code"));
    }
    private void Authorize(MessageQueueAddress address, AuthorityBit bits, bool replyDelivery = false)
    {
        if (address.Named is not { } queue) { AuthorizeProgram(address, replyDelivery); return; }
        ValidateQueue(queue); new ServiceAuthorization(factory).RequireObject(queue.Library, queue.Name.Value, ObjectType.MessageQueue, bits);
        if (new SqliteObjectStore(factory).GetForAuthorization(queue.Library, queue.Name.Value, ObjectType.MessageQueue) is null) throw new CpfException("CPF9801", "Message queue does not exist.");
    }
    private static void ValidateQueue(QualifiedName queue)
    {
        if (!ObjectName.IsValid(queue.Library) || !ObjectName.IsValid(queue.Name.Value) || queue.Library != queue.Library.ToUpperInvariant() || queue.Name.Value != queue.Name.Value.ToUpperInvariant()) throw Invalid("A resolved uppercase queue name is required.");
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, MessageQueueAddress queue, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$lib", (object?)queue.Named?.Library ?? DBNull.Value); command.Parameters.AddWithValue("$name", (object?)queue.Named?.Name.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$program", (object?)queue.ProgramQueue ?? DBNull.Value); return command;
    }
    private static CpfException Invalid(string message) => new("IPC0003", message);
}
