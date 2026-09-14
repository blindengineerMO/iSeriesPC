using Ipc.Cl.Parsing;
namespace Ipc.Cl.Interpreter;

public enum ClStatementKind
{
    Program,
    Declare,
    Change,
    If,
    Else,
    EndIf,
    Goto,
    Label,
    Call,
    SendProgramMessage,
    Command,
    EndProgram,
}

public sealed class ClStatement
{
    public ClStatementKind Kind { get; init; }

    public string? VariableName { get; init; }

    public string? Value { get; init; }

    public string? Condition { get; init; }

    public string? Then { get; init; }

    public string? Label { get; init; }

    public string? ProgramName { get; init; }

    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    public CommandCall? Command { get; init; }

    public int Jump { get; set; } = -1;

    public IReadOnlyList<string> EntryParameters { get; init; } = Array.Empty<string>();

    public override string ToString() => Kind switch
    {
        ClStatementKind.Program => "PGM",
        ClStatementKind.Declare => $"DCL VAR({VariableName}) VALUE({Value})",
        ClStatementKind.Change => $"CHGVAR VAR({VariableName}) VALUE({Value})",
        ClStatementKind.If => $"IF COND({Condition}){(Then is null ? string.Empty : $" THEN({Then})")}",
        ClStatementKind.Else => "ELSE",
        ClStatementKind.EndIf => "ENDIF",
        ClStatementKind.Goto => $"GOTO CMDLBL({Label})",
        ClStatementKind.Label => $"{Label}:",
        ClStatementKind.Call => $"CALL PGM({ProgramName})",
        ClStatementKind.SendProgramMessage => $"SNDPGMMSG MSG({Value})",
        ClStatementKind.Command => Command?.ToString() ?? string.Empty,
        _ => string.Empty,
    };
}