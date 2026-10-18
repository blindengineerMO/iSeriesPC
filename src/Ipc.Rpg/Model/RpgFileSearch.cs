namespace Ipc.Rpg.Model;

public enum RpgFileSearchKind { Scalar, Values, DataStructure, KeyList, Current }

/// <summary>Source-bound search components. Values are evaluated at each I/O operation.</summary>
public sealed class RpgFileSearch
{
    public required RpgFileSearchKind Kind { get; init; }
    public IReadOnlyList<string> Expressions { get; init; } = [];
    public string? DataStructure { get; init; }
    public string? CountExpression { get; init; }
}
