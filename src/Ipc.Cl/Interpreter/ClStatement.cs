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
    Branch,
    ForBegin,
    ForEnd,
    ReceiveFile,
    CloseFile,
    NoOp,
}

public sealed class ClStatement
{
    public Ipc.Core.Compilation.SourceLocation? Location { get; set; }

    public bool FileVariable { get; init; }
    public string? OpenId { get; init; }
    public List<ClMessageMonitor> Monitors { get; } = new();
    public string MessageType { get; init; } = "*INFO";
    public string? MessageId { get; init; }

    public ClExpression? Expression { get; set; }
    public ClExpression? TerminalExpression { get; set; }
    public ClExpression? TargetExpression { get; set; }
    public string? DeclarationType { get; init; }
    public string? DeclarationLength { get; init; }
    public long Increment { get; init; } = 1;
    public int LoopHead { get; init; } = -1;

    public ClStatementKind Kind { get; init; }

    public string? VariableName { get; init; }

    public string? Value { get; init; }

    public string? Condition { get; init; }

    public string? Then { get; init; }

    public string? Label { get; init; }

    public string? ProgramName { get; init; }

    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ClCallArgument> CallArguments { get; set; } = Array.Empty<ClCallArgument>();

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
