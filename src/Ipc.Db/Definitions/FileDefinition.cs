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

    public int DeclaredDigits { get; init; }

    public bool CurrentDatetimeDefault { get; init; }

    public FieldDefault? DefaultValue { get; set; }

    public int Position { get; set; }

    public bool VariableLength { get; set; }

    public bool NullCapable { get; set; }

    public int Ccsid { get; init; } = 37;

    public int Sequence { get; set; }

    public bool Descending { get; set; }

    public string? Text { get; init; }

    public bool IsNumeric => Type is FieldType.Zoned or FieldType.Packed or FieldType.Binary or FieldType.Float;

    [JsonIgnore]
    public int StorageLength => Type == FieldType.Packed ? checked((Length + 2) / 2) : checked(Length + (VariableLength ? 2 : 0));
}

public sealed record FieldDefault(string? Value);

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
            offset = checked(offset + field.StorageLength);
        }

        RecordLength = offset - 1;
        BufferLayoutVersion = 2;
    }

    [JsonInclude]
    public int RecordLength { get; private set; }

    [JsonInclude]
    public int BufferLayoutVersion { get; private set; } = 1;
}

public sealed class FileDefinition
{
    public required string Name { get; init; }

    public required FileAttribute Attribute { get; init; }

    public bool Unique { get; init; }

    public bool ExcludeNullKeys { get; init; }

    public int MaximumMembers { get; set; } = 32767;

    public List<RecordFormat> Formats { get; set; } = new();

    [JsonIgnore]
    public RecordFormat PrimaryFormat => Formats.First();

    public string? Text { get; init; }

    public int Ccsid { get; init; } = 37;

    public LogicalFileDefinition? Logical { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static FileDefinition FromJson(string json)
    {
        var definition = JsonSerializer.Deserialize<FileDefinition>(json, JsonOptions) ?? throw new InvalidOperationException("Invalid file definition.");
        foreach (var format in definition.Formats)
        {
            // Version 1 counted decimal digits as bytes and omitted VARLEN prefixes.
            // Member tables contain typed SQL values, so adapting buffer metadata
            // does not rewrite data or claim compatibility with old binary buffers.
            if (format.BufferLayoutVersion == 1) format.AssignPositions();
            else if (format.BufferLayoutVersion != 2) throw new InvalidDataException("Unsupported record buffer layout version.");
        }
        return definition;
    }
}

public enum FileAttribute
{
    Physical,
    Source,
    Logical,
}
