using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Dsp;

public enum PanelFieldUsage { Output, Input, Both, Hidden }
public enum PanelFieldType { Character, Numeric, Date, Time, Timestamp }
public sealed record PanelCondition(int Indicator, bool Negated = false)
{
    public bool Matches(IReadOnlyList<bool> indicators) => indicators[Indicator] != Negated;
}
public sealed record PanelKeyword(string Name, string[] Arguments, PanelCondition[] Conditions, int Line, string? BoundText = null)
{
    public bool Enabled(IReadOnlyList<bool> indicators) => Conditions.All(c => c.Matches(indicators));
}
public sealed class PanelField
{
    public required string Name { get; init; }
    public bool Constant { get; init; }
    public int Length { get; set; }
    public int Decimals { get; init; }
    public PanelFieldUsage Usage { get; set; }
    public PanelFieldType Type { get; init; }
    public char KeyboardShift { get; init; } = 'A';
    public int Row { get; init; }
    public int Column { get; init; }
    public int Line { get; init; }
    public PanelCondition[] Conditions { get; init; } = Array.Empty<PanelCondition>();
    public List<PanelKeyword> Keywords { get; init; } = new();
    public int ScreenLength
    {
        get
        {
            if (Type != PanelFieldType.Numeric) return Length;
            var width = Length + (Decimals > 0 ? 1 : 0) + 1;
            foreach (var keyword in Keywords)
            {
                if (keyword.Name == "EDTWRD") width = Math.Max(width, keyword.Arguments[0].Length);
                if (keyword.Name == "EDTCDE" && keyword.Arguments[0] is "1" or "2") width = Math.Max(width, Length + (Decimals > 0 ? 1 : 0) + Math.Max(0, (Length - Decimals - 1) / 3));
            }
            return width;
        }
    }
    public string BindingName => Keywords.FirstOrDefault(x => x.Name == "ALIAS")?.Arguments[0] ?? Name;
}
public sealed class PanelRecord
{
    public required string Name { get; init; }
    public int Line { get; init; }
    public List<PanelKeyword> Keywords { get; init; } = new();
    public List<PanelField> Fields { get; init; } = new();
    public List<PanelHelpArea> HelpAreas { get; init; } = new();
}
public sealed class PanelDefinition
{
    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    public int Rows { get; set; } = 24;
    public int Columns { get; set; } = 80;
    public List<PanelKeyword> Keywords { get; init; } = new();
    public List<PanelRecord> Records { get; init; } = new();
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static PanelDefinition FromJson(string json)
    {
        var result = JsonSerializer.Deserialize<PanelDefinition>(json, Json) ?? throw new InvalidDataException("Missing display definition.");
        if (result.Version != 1) throw new InvalidDataException("Unsupported display definition version.");
        return result;
    }
}
public sealed class PanelCompileException(string source, int line, int column, string token, string detail) : Exception(
    $"IPC0006: {source}:{line}:{column}: {token}: {detail}")
{
    public string SourceName { get; } = source;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string Token { get; } = token;
}
