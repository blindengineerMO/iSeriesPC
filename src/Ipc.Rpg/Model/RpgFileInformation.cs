namespace Ipc.Rpg.Model;

/// <summary>A program-local feedback binding, independent of the external record layout.</summary>
public sealed record RpgFileInformation(string File, string Structure, int DeclarationLine);
public sealed record RpgFileErrorHandler(string File, string Routine, int DeclarationLine);
