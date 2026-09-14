namespace Ipc.Cl.Interpreter;

public sealed class ClProgram
{
    public required string Name { get; init; }

    public required string Library { get; init; }

    public IReadOnlyList<ClStatement> Statements { get; init; } = Array.Empty<ClStatement>();

    public IReadOnlyDictionary<string, int> Labels { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> EntryParameters { get; init; } = Array.Empty<string>();
}

public sealed class ClProgramStore
{
    public required string Name { get; init; }

    public required string Library { get; init; }

    public required string Source { get; init; }

    public string? Description { get; init; }
}