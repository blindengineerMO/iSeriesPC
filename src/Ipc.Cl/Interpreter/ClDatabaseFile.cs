using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Objects;

namespace Ipc.Cl.Interpreter;

public sealed record ClFileRequest(string File, string RecordFormat, string OpenId, bool AllowVariableLength, bool AllowNull, bool BinaryAsInteger);
public sealed record ClFileField(string Name, string Type, int Length, int Decimals);
public sealed record ClDatabaseFile(string ResolvedFile, string RecordFormat, string FormatSignature, IReadOnlyList<ClFileField> Fields);
public sealed record ClFileBinding(ClFileRequest Request, ClDatabaseFile Definition);
public sealed record ClFileReadResult(IReadOnlyDictionary<string, object?> Values, string? ErrorId = null, string? Error = null);

public sealed class ClDatabaseCursor(Func<ClFileReadResult?> read, Action dispose, Action? close = null) : IDisposable
{
    private bool _disposed;
    public ClFileReadResult? Read() { ObjectDisposedException.ThrowIf(_disposed, this); return read(); }
    public void Close() { if (_disposed) return; _disposed = true; (close ?? dispose)(); }
    public void Dispose() { if (_disposed) return; _disposed = true; dispose(); }
}

/// <summary>Compile-time file layouts bound to the expanded source, so CALL never
/// silently recompiles file variables against a changed database definition.</summary>
public sealed record ClFileBindings(int Version, string TextHash, IReadOnlyList<ClFileBinding> Files)
{
    public const string AttributeName = "ipc.cl.files";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static string Serialize(ClProgram program)
    {
        var value = new ClFileBindings(1, Hash(program.Source!.Text), program.Files);
        var json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > 1048576) throw Invalid();
        return json;
    }
    public static ClFileBindings Restore(string text, string json)
    {
        try
        {
            if (text.Length > 4194304 || Encoding.UTF8.GetByteCount(text) > 4194304 || json.Length > 1048576 || Encoding.UTF8.GetByteCount(json) > 1048576) throw Invalid();
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 16 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            var bindings = JsonSerializer.Deserialize<ClFileBindings>(json, Json) ?? throw Invalid();
            if (bindings.Version != 1 || bindings.TextHash != Hash(text) || bindings.Files is null || bindings.Files.Count > 5 ||
                bindings.Files.Any(f => f is null || f.Request is null || f.Definition is null) || bindings.Files.Select(f => f.Request.OpenId).Distinct(StringComparer.Ordinal).Count() != bindings.Files.Count) throw Invalid();
            foreach (var file in bindings.Files)
            {
                var request = file.Request;
                var names = request.File?.Split('/');
                if (names is null || names.Length is < 1 or > 2 || !ObjectName.IsValid(names[^1]) ||
                    names.Length == 2 && names[0] is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(names[0]) ||
                    request.RecordFormat != "*ALL" && !ObjectName.IsValid(request.RecordFormat) || request.OpenId != "*NONE" && !ObjectName.IsValid(request.OpenId)) throw Invalid();
                Validate(file.Definition);
            }
            return bindings;
        }
        catch (JsonException) { throw Invalid(); }
    }
    public ClDatabaseFile Resolve(ClFileRequest request) => Files.SingleOrDefault(f => f.Request == request)?.Definition ?? throw Invalid();
    public static void Validate(ClDatabaseFile definition)
    {
        if (definition.ResolvedFile is null || definition.ResolvedFile.Split('/') is not { Length: 2 } names || names.Any(n => !ObjectName.IsValid(n)) ||
            !ObjectName.IsValid(definition.RecordFormat) || definition.FormatSignature is null || definition.FormatSignature.Length != 64 || definition.FormatSignature.Any(c => !char.IsAsciiHexDigit(c)) ||
            definition.Fields is null || definition.Fields.Count is < 1 or > 1024 || definition.Fields.Any(f => f is null || !ObjectName.IsValid(f.Name)) ||
            definition.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != definition.Fields.Count) throw Invalid();
        foreach (var field in definition.Fields)
        {
            var valid = field.Type switch { "*CHAR" => field.Length is >= 1 and <= 32767 && field.Decimals == 0,
                "*DEC" => field.Length is >= 1 and <= 15 && field.Decimals >= 0 && field.Decimals <= Math.Min(field.Length, 9),
                "*INT" or "*UINT" => field.Length is 2 or 4 && field.Decimals == 0, "*LGL" => field.Length == 1 && field.Decimals == 0, _ => false };
            if (!valid) throw Invalid();
        }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("Invalid or stale compiled CL file bindings.");
}
