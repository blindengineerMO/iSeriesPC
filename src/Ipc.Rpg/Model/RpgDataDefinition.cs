namespace Ipc.Rpg.Model;

public sealed record RpgDataDefinition(string Name, string Symbol, bool Import, int Line);
public sealed record RpgDataExport(RpgProgram Module, RpgDataDefinition Definition);
internal sealed record RpgDataBinding(RpgDataExport? Static, RpgServiceImport? Service);
