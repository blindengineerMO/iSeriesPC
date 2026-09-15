namespace Ipc.Db.Definitions;

public sealed record LogicalCondition(string Field, string Operator, IReadOnlyList<string> Values);
public sealed record LogicalRule(bool Select, IReadOnlyList<LogicalCondition> Conditions);
public sealed record LogicalFileDefinition
{
    public required string SourceLibrary { get; init; }
    public required string SourceFile { get; init; }
    public required string SourceFormat { get; init; }
    public IReadOnlyList<LogicalRule> Rules { get; init; } = Array.Empty<LogicalRule>();
    public bool DefaultSelect { get; init; } = true;
    public bool Unique { get; init; }
    public bool ExcludeNullKeys { get; init; }
    public int MaximumMembers { get; init; } = 256;
    public IReadOnlyDictionary<string, string> UniquePaths { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Members { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}
