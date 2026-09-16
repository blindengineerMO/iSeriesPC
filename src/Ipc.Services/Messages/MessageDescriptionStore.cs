using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Messages;

/// <summary>Bounded, versioned message descriptions shared by display and program messages.</summary>
public sealed class MessageDescriptionStore(SqliteConnectionFactory factory)
{
    private sealed record LegacyPayload(int Version, Dictionary<string, string> Messages);
    private sealed record Payload(int Version, Dictionary<string, MessageDescription> Messages);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int MaximumPayloadBytes = 8388608;
    public void Create(string library, string name, string? description = null, int ccsid = 37)
    {
        if (!CodePage.IsSupported(ccsid)) throw Invalid("Unsupported message-file CCSID.");
        try { new SqliteObjectStore(factory).Create(new ObjectDescriptor {
            Key = new(library, name), ObjectType = ObjectType.MessageFile, Description = description, Ccsid = ccsid,
            Owner = OperationIdentity.Current?.Principal ?? "QSECOFR", Source = JsonSerializer.Serialize(new Payload(2, new()), Json) }); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode == 1555 || error.SqliteExtendedErrorCode == 2067)
        { throw new CpfException("CPF7302", "Message file already exists."); }
    }
    public void Add(string library, string name, string id, string text)
        => Add(library, name, id, new MessageDescription(text, "", 0, Array.Empty<MessageDataField>(),
            new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.MessageFile).Ccsid));
    public void Add(string library, string name, string id, MessageDescription description)
    {
        ValidateId(id); MessageDescriptionFormat.Validate(description);
        Mutate(library, name, Authorities.UseBits | AuthorityBit.Add, (payload, ccsid) => {
            if (payload.Messages.ContainsKey(id)) throw new CpfException("CPF2412", "Message description already exists.");
            if (payload.Messages.Count >= 4096) throw Invalid("Message file limit is 4096 descriptions.");
            var stored = description with { Ccsid = ccsid }; MessageDescriptionFormat.Validate(stored);
            payload.Messages.Add(id, Freeze(stored));
        });
    }
    public void Change(string library, string name, string id, string? text = null, string? secondLevel = null,
        int? severity = null, IReadOnlyList<MessageDataField>? fields = null, string? defaultReply = null, bool replaceDefault = false)
    {
        ValidateId(id);
        Mutate(library, name, Authorities.UseBits | AuthorityBit.Update, (payload, _) => {
            if (!payload.Messages.TryGetValue(id, out var previous)) throw Missing();
            var changed = previous with { Text = text ?? previous.Text, SecondLevel = secondLevel ?? previous.SecondLevel,
                Severity = severity ?? previous.Severity, Fields = fields ?? previous.Fields,
                DefaultReply = replaceDefault ? defaultReply : previous.DefaultReply,
                LegacyLiteral = previous.LegacyLiteral && text is null && fields is null };
            MessageDescriptionFormat.Validate(changed, legacyText: text is null);
            payload.Messages[id] = Freeze(changed);
        });
    }
    public void Remove(string library, string name, string id)
    {
        if (id != "*ALL") ValidateId(id);
        Mutate(library, name, AuthorityBit.ObjectOperate | AuthorityBit.Delete, (payload, _) => {
            if (id == "*ALL") payload.Messages.Clear();
            else if (!payload.Messages.Remove(id)) throw Missing();
        });
    }
    public string Get(string library, string name, string id) => GetDescription(library, name, id).Text;
    public MessageDescription GetDescription(string library, string name, string id)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.MessageFile);
        return Parse(descriptor.Source, descriptor.Ccsid).Messages.GetValueOrDefault(id) is { } value ? Freeze(value) : throw Missing();
    }
    public PredefinedMessage Snapshot(string library, string name, string id, string requestedLibrary, Ipc.Core.Work.ProgramBuffer data)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.MessageFile);
        var definition = Parse(descriptor.Source, descriptor.Ccsid).Messages.GetValueOrDefault(id) ?? throw Missing();
        var result = new PredefinedMessage(library, name, requestedLibrary, descriptor.Created, Freeze(definition), data);
        _ = result.Serialize(); return result;
    }
    public IReadOnlyList<KeyValuePair<string, MessageDescription>> List(string library, string name)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.MessageFile);
        // IBM message ordering uses alphabetic suffixes before numeric suffixes.
        var comparer = Comparer<string>.Create((left, right) => CompareIds(left, right));
        return Parse(descriptor.Source, descriptor.Ccsid).Messages.OrderBy(pair => pair.Key, comparer)
            .Select(pair => new KeyValuePair<string, MessageDescription>(pair.Key, Freeze(pair.Value))).ToArray();
    }
    private void Mutate(string library, string name, AuthorityBit authority, Action<Payload, int> change)
    {
        var authorization = new ServiceAuthorization(factory);
        authorization.RequireObject(library, name, ObjectType.MessageFile, authority);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        authorization.RequireObject(library, name, ObjectType.MessageFile, authority);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source,ccsid FROM sys_objects WHERE lib=$lib AND name=$name AND type='*MSGF'";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", name);
        Payload payload; int ccsid;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new CpfException("CPF2407", "Message file not found.");
            ccsid = reader.GetInt32(1); payload = Parse(reader.IsDBNull(0) ? null : reader.GetString(0), ccsid);
        }
        change(payload, ccsid);
        var json = JsonSerializer.Serialize(payload, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw Invalid("Message file exceeds 8 MiB.");
        command.CommandText = "UPDATE sys_objects SET source=$source,changed=$now WHERE lib=$lib AND name=$name AND type='*MSGF'";
        command.Parameters.AddWithValue("$source", json); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new CpfException("CPF2407", "Message file not found.");
        transaction.Commit();
    }
    private static Payload Parse(string? json, int ccsid)
    {
        if (json is null || json.Length > MaximumPayloadBytes || Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw Invalid("Invalid message-file payload size.");
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 16 });
            var names = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) names.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) names.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !names.Peek().Add(reader.GetString()!)) throw Invalid("Duplicate message-file property.");
            }
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number)) throw Invalid("Missing message-file version.");
            Payload payload;
            if (number == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyPayload>(json, Json);
                if (legacy?.Messages is null) throw Invalid("Invalid legacy message file.");
                payload = new(2, legacy.Messages.ToDictionary(pair => pair.Key,
                    pair => new MessageDescription(pair.Value, "", 0, Array.Empty<MessageDataField>(), ccsid, LegacyLiteral: true)));
            }
            else if (number == 2) payload = JsonSerializer.Deserialize<Payload>(json, Json) ?? throw Invalid("Invalid message file.");
            else throw Invalid("Unsupported message-file version.");
            if (payload.Messages is null || payload.Messages.Count > 4096) throw Invalid("Invalid message count.");
            foreach (var pair in payload.Messages)
            {
                if (pair.Value is null || pair.Key.Length != 7 || !pair.Key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))) throw Invalid("Invalid stored message identifier.");
                MessageDescriptionFormat.Validate(pair.Value, legacyText: true);
            }
            return payload;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw Invalid("Invalid message-file payload."); }
    }
    private static MessageDescription Freeze(MessageDescription value) => value with { Fields = Array.AsReadOnly(value.Fields.ToArray()) };
    public static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, @"\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}\z")) throw new CpfException("CPF2499", "Invalid message identifier.");
    }
    private static int CompareIds(string left, string right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var a = char.IsAsciiDigit(left[i]) ? left[i] + 128 : left[i];
            var b = char.IsAsciiDigit(right[i]) ? right[i] + 128 : right[i];
            if (a != b) return a.CompareTo(b);
        }
        return left.Length.CompareTo(right.Length);
    }
    private static CpfException Missing() => new("CPF2419", "Message description not found.");
    private static CpfException Invalid(string message) => new("IPC0127", message);
}
