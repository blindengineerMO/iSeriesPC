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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReuseDeletedRecords { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileMemberSize? MemberSize { get; set; }

    public int RecordLockWaitSeconds { get; set; } = 60;

    public List<RecordFormat> Formats { get; set; } = new();

    [JsonIgnore]
    public RecordFormat PrimaryFormat => Formats.First();

    public string? Text { get; init; }

    public int Ccsid { get; init; } = 37;

    public LogicalFileDefinition? Logical { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SqlIndex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SqlTableDefinition? SqlTable { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SqlViewDefinition? SqlView { get; set; }

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

[method: JsonConstructor]
public sealed record SqlTableDefinition(IReadOnlyList<SqlUniqueConstraint> Constraints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SqlCheckConstraint>? Checks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SqlForeignConstraint>? ForeignKeys = null)
{
    public SqlTableDefinition(IReadOnlyList<SqlUniqueConstraint> constraints) : this(constraints, null) { }
    public SqlTableDefinition(IReadOnlyList<SqlUniqueConstraint> constraints, IReadOnlyList<SqlCheckConstraint>? checks) : this(constraints, checks, null) { }
    public void Deconstruct(out IReadOnlyList<SqlUniqueConstraint> constraints) => constraints = Constraints;
    public void Deconstruct(out IReadOnlyList<SqlUniqueConstraint> constraints, out IReadOnlyList<SqlCheckConstraint>? checks) { constraints = Constraints; checks = Checks; }
}
public enum SqlForeignDelete { NoAction, Restrict, Cascade, SetNull, SetDefault }
public enum SqlForeignUpdate { NoAction, Restrict }
public sealed record SqlForeignConstraint(string? Name, IReadOnlyList<string> Columns, Ipc.Db.Sql.SqlObjectName Parent,
    IReadOnlyList<string>? ParentColumns = null, SqlForeignDelete DeleteRule = SqlForeignDelete.NoAction,
    SqlForeignUpdate UpdateRule = SqlForeignUpdate.NoAction, string? ParentMember = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Defined { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CheckPending { get; init; }
}
public sealed record SqlUniqueConstraint(string? Name, bool Primary, IReadOnlyList<string> Columns);
public sealed record SqlCheckConstraint(string? Name, string Condition, int Ccsid = 37,
    Ipc.Db.Sql.SqlConversionOptions? Conversion = null, string? Column = null, string? Qualifier = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CheckPending { get; init; }
}
public sealed record SqlViewSource(string Library, string Name, IReadOnlyList<string> Columns);
public enum SqlViewCheckOption { None, Local, Cascaded }
public sealed record SqlViewDefinition(string Query, Ipc.Db.Sql.SqlExecutionOptions Options,
    IReadOnlyList<SqlViewSource> Sources, IReadOnlyList<Ipc.Db.Sql.SqlQueryColumn> Columns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SqlViewCheckOption CheckOption = SqlViewCheckOption.None);

public enum FileAttribute
{
    Physical,
    Source,
    Logical,
}
