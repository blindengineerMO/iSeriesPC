namespace Ipc.Cl.Commands;

public sealed record CommandParameter(string Keyword, string Prompt, bool Literal = false, bool Secret = false, int MaximumLength = 1024, int Position = -1, string? DefaultValue = null);
public sealed record CommandMetadata(string Name, IReadOnlyList<CommandParameter> Parameters, int MaximumPositional = 0, Ipc.Core.Menu.HelpRequest? Help = null, string? Revision = null);
