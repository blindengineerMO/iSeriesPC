using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Dsp;

public sealed record HelpLink(string Module, string? PanelGroup = null, int Id = 0);
public sealed record HelpSpan(string Text, string Style = "PLAIN", HelpLink? Link = null);
public sealed class HelpBlock
{
    public string Kind { get; init; } = "P";
    public List<HelpSpan> Spans { get; init; } = new();
}
public sealed class HelpModule
{
    public required string Name { get; init; }
    public string Title { get; set; } = "";
    public List<string> IndexWords { get; init; } = new();
    public List<HelpBlock> Blocks { get; init; } = new();
}
public sealed class HelpDefinition
{
    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    public List<HelpModule> Modules { get; init; } = new();
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static HelpDefinition FromJson(string json)
    {
        var result = JsonSerializer.Deserialize<HelpDefinition>(json, Json) ?? throw new InvalidDataException("Missing panel group definition.");
        if (result.Version != 1) throw new InvalidDataException("Unsupported panel group version.");
        return result;
    }
}
public sealed class UimCompileException(string source, int line, int column, string token, string message) : Exception($"IPC0006: {source}:{line}:{column}: {token}: {message}")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string Token { get; } = token;
}
