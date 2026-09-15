using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Core.Compilation;

/// <summary>Source snapshots and locations bound to the expanded program text.</summary>
public sealed record CompiledSourceMap(int Version, string TextHash, IReadOnlyList<SourceLocation> Locations, IReadOnlyDictionary<string, string> Sources)
{
    public const string AttributeName = "ipc.source.map";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    public static string Serialize(PreprocessedSource source)
    {
        var json = JsonSerializer.Serialize(new CompiledSourceMap(1, Hash(source.Text), source.Locations, source.Sources), Json);
        _ = Restore(source.Text, json); return json;
    }
    public static PreprocessedSource Restore(string text, string serialized)
    {
        try
        {
            if (text.Length > 4194304 || Encoding.UTF8.GetByteCount(text) > 4194304 || serialized.Length > 16777216) throw Invalid();
            var bytes = Encoding.UTF8.GetBytes(serialized);
            if (bytes.Length > 16777216) throw Invalid();
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            var map = JsonSerializer.Deserialize<CompiledSourceMap>(bytes, Json) ?? throw Invalid();
            if (map.Version != 1 || map.TextHash != Hash(text) || map.Sources is null || map.Sources.Count is < 1 or > 64 || map.Locations is null || map.Locations.Count > 50000 ||
                (text.Length == 0 ? map.Locations.Count > 1 : map.Locations.Count != text.Count(c => c == '\n') + 1)) throw Invalid();
            var sourceLines = new Dictionary<string, string[]>(); var inputBytes = 0;
            foreach (var pair in map.Sources)
            {
                if (!Identity(pair.Key) || pair.Value is null || pair.Value.Length > 1048576 || pair.Value.Contains('\0')) throw Invalid();
                var count = Encoding.UTF8.GetByteCount(pair.Value); inputBytes += count;
                if (count > 1048576 || inputBytes > 4194304) throw Invalid();
                var lines = pair.Value.Split('\n'); if (lines.Length > 10000) throw Invalid();
                sourceLines.Add(pair.Key, lines);
            }
            foreach (var location in map.Locations)
            {
                if (location is null || !Identity(location.Source) || !sourceLines.TryGetValue(location.Source, out var lines) || location.Line < 1 || location.Line > lines.Length ||
                    location.Column < 1 || location.Column > lines[location.Line - 1].Length + 1 || location.IncludeStack is null || location.IncludeStack.Count >= 16 ||
                    location.IncludeStack.Any(s => string.IsNullOrEmpty(s) || s.Length > 1100 || s.Any(char.IsControl))) throw Invalid();
            }
            return new(text, map.Locations, map.Sources);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw Invalid(); }
    }
    private static bool Identity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static InvalidDataException Invalid() => new("Invalid or stale compiled source map; recompile the program from source.");
}
