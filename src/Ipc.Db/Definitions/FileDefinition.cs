using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ipc.Db.Definitions;

public enum FieldType
{
    Alpha,
    Zoned,
    Packed,
    Binary,
    Float,
    Logic,
    Date,
    Time,
    Timestamp,
}

public enum FieldUsage
{
    Both,
    Input,
    Output,
    Hidden,
}

public enum CollatingOrder
{
    Hexadecimal,
    Ffo,
}

public sealed class KeySpec
{
    public required string Field { get; init; }

    public bool Descending { get; init; }
}

public sealed class FieldSpec
{
    public required string Name { get; init; }

    public required FieldType Type { get; init; }

    public required int Length { get; init; }

    public int Decimals { get; init; }

    public int Position { get; set; }

    public bool VariableLength { get; set; }

    public bool NullCapable { get; set; }

    public int Ccsid { get; init; } = 37;

    public int Sequence { get; set; }

    public bool Descending { get; set; }

    public string? Text { get; init; }

    public bool IsNumeric => Type is FieldType.Zoned or FieldType.Packed or FieldType.Binary or FieldType.Float;
}

public sealed class RecordFormat
{
    public required string Name { get; init; }

    public List<FieldSpec> Fields { get; set; } = new();

    public string? Text { get; init; }

    public FieldSpec? Find(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AssignPositions()
    {
        var offset = 1;
        foreach (var field in Fields)
        {
            field.Position = offset;
            offset += field.Length;
        }

        RecordLength = offset - 1;
    }

    [JsonInclude]
    public int RecordLength { get; private set; }
}

public sealed class FileDefinition
{
    public required string Name { get; init; }

    public required FileAttribute Attribute { get; init; }

    public List<RecordFormat> Formats { get; set; } = new();

    [JsonIgnore]
    public RecordFormat PrimaryFormat => Formats.First();

    public string? Text { get; init; }

    public int Ccsid { get; init; } = 37;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static FileDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<FileDefinition>(json, JsonOptions)
        ?? throw new InvalidOperationException("Invalid file definition.");
}

public enum FileAttribute
{
    Physical,
    Source,
    Logical,
}