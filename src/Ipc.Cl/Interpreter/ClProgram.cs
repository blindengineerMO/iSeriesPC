namespace Ipc.Cl.Interpreter;

public sealed class ClProgram
{
    public Ipc.Core.Compilation.PreprocessedSource? Source { get; init; }

    public required string Name { get; init; }

    public required string Library { get; init; }

    public IReadOnlyList<ClFileBinding> Files { get; init; } = Array.Empty<ClFileBinding>();

    public IReadOnlyList<ClMessageMonitor> Monitors { get; init; } = Array.Empty<ClMessageMonitor>();

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
