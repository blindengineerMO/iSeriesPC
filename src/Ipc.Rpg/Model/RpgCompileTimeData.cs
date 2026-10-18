namespace Ipc.Rpg.Model;

internal sealed record RpgCompileTimeData(string? Name, int Line)
{
    internal List<(string Text, int Line)> Records { get; } = new();
}
