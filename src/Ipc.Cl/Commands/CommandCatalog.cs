using Ipc.Cl.Parsing;
namespace Ipc.Cl.Commands;

public enum CommandOutcome
{
    Continue,
    SignOff,
    GoMenu,
    Error,
}

public sealed class CommandResult
{
    public CommandOutcome Outcome { get; init; }

    public string? Message { get; init; }

    public string? MenuName { get; init; }

    public string? MenuLibrary { get; init; }

    public IReadOnlyList<string>? Listing { get; init; }

    public bool IsError => Outcome == CommandOutcome.Error;

    public static CommandResult Ok(string? message = null) =>
        new() { Outcome = CommandOutcome.Continue, Message = message };

    public static CommandResult SignOff(string? message = null) =>
        new() { Outcome = CommandOutcome.SignOff, Message = message };

    public static CommandResult Go(string menuName, string? menuLibrary = null) =>
        new() { Outcome = CommandOutcome.GoMenu, MenuName = menuName, MenuLibrary = menuLibrary };

    public static CommandResult Error(string message) =>
        new() { Outcome = CommandOutcome.Error, Message = message };
}

public sealed class CommandCatalog
{
    private readonly Dictionary<string, Func<CommandCall, CommandResult>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, Func<CommandCall, CommandResult> handler)
    {
        _handlers[name] = handler;
    }

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
            return CommandResult.Error(ex.Message);
        }

        if (!_handlers.TryGetValue(call.Name, out var handler))
        {
            return CommandResult.Error($"Command {call.Name} not found.");
        }

        return handler(call);
    }

    public CommandResult Execute(CommandCall call)
    {
        if (!_handlers.TryGetValue(call.Name, out var handler))
        {
            return CommandResult.Error($"Command {call.Name} not found.");
        }

        return handler(call);
    }
}