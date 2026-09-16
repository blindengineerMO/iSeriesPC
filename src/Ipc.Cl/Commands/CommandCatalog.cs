using Ipc.Cl.Parsing;
using Ipc.Cl.Compatibility;
namespace Ipc.Cl.Commands;

public enum CommandOutcome
{
    Continue,
    SignOff,
    GoMenu,
    Error,
    DisplayPanel,
    DisplayHelp,
    ScreenDesigner,
    GroupJob,
}

public sealed class CommandResult
{
    public CommandOutcome Outcome { get; init; }

    public string? Message { get; init; }
    public string? MessageId { get; init; }
    public string? MessageData { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Ipc.Core.Work.ProgramBuffer? MessageDataBuffer { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Ipc.Core.Work.ProgramMessageReference? ExceptionReference { get; init; }

    public PanelRequest? Panel { get; init; }
    public Ipc.Core.Menu.HelpRequest? Help { get; init; }
    public DesignerRequest? Designer { get; init; }
    public GroupJobRequest? GroupJob { get; init; }

    public string? MenuName { get; init; }

    public string? MenuLibrary { get; init; }

    public IReadOnlyList<string>? Listing { get; init; }
    public Ipc.Core.Menu.WorkList? WorkList { get; init; }

    public bool IsError => Outcome == CommandOutcome.Error;

    public static CommandResult Ok(string? message = null) =>
        new() { Outcome = CommandOutcome.Continue, Message = message };

    public static CommandResult SignOff(string? message = null) =>
        new() { Outcome = CommandOutcome.SignOff, Message = message };

    public static CommandResult Go(string menuName, string? menuLibrary = null) =>
        new() { Outcome = CommandOutcome.GoMenu, MenuName = menuName, MenuLibrary = menuLibrary };

    public static CommandResult Error(string message, string? messageId = null, string? messageData = null,
        Ipc.Core.Work.ProgramMessageReference? exceptionReference = null, Ipc.Core.Work.ProgramBuffer? messageDataBuffer = null) =>
        new() { Outcome = CommandOutcome.Error, Message = message,
            MessageId = messageId ?? (System.Text.RegularExpressions.Regex.IsMatch(message, @"\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}:") ? message[..7] : "IPC0006"),
            MessageData = messageData, ExceptionReference = exceptionReference, MessageDataBuffer = messageDataBuffer };
}

public sealed record GroupJobRequest(string Action, string Name, string InitialProgram = "QCMD", string Description = "");

public sealed record DesignerRequest(string Library, string SourceFile, string Member);

public sealed record PanelRequest(string Library, string File, string Record);

public sealed class CommandCatalog
{
    private readonly bool _builtinContracts;

    public CommandCatalog(bool builtinContracts = false) => _builtinContracts = builtinContracts;

    private readonly Dictionary<string, Func<CommandCall, CommandResult>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, Func<CommandCall, CommandResult> handler)
    {
        _handlers[name] = handler;
    }

    public IReadOnlyList<string> RegisteredNames => _handlers.Keys.Order(StringComparer.Ordinal).ToArray();

    public bool IsRegistered(string name) => _handlers.ContainsKey(name);

    public CommandResult Execute(string line)
    {
        CommandCall call;
        try
        {
            call = CommandParser.Parse(line);
        }
        catch (ClParseException ex)
        {
            return CommandResult.Error($"IPC0005: {ex.Message}");
        }
        return Execute(call);
    }

    public CommandResult Execute(CommandCall call)
    {
        var name = _builtinContracts ? BuiltinContract.CanonicalName(call.Name) : call.Name;
        if (!_handlers.TryGetValue(name, out var handler))
        {
            return CommandResult.Error(_builtinContracts ? BuiltinContract.Unavailable(call.Name) : $"IPC0001: Command {call.Name} not found.");
        }
        if (_builtinContracts && BuiltinContract.Validate(call) is { } error)
            return CommandResult.Error(error);
        try { return handler(call); }
        catch (Ipc.Core.Messages.CpfException ex) { return CommandResult.Error(ex.Message); }
        catch (ArgumentException) when (_builtinContracts) { return CommandResult.Error("IPC0003: Invalid command parameter."); }
    }
}
