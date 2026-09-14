using Ipc.Cl.Parsing;
using System.Text;
using Ipc.Cl.Commands;

namespace Ipc.Cl.Interpreter;

public sealed class ClInterpreter
{
    private readonly Func<string, string, ClProgram?> _programLoader;
    private readonly Func<CommandCall, CommandResult> _commandRunner;
    private readonly Action<string>? _messageSink;

    public ClInterpreter(
        Func<string, string, ClProgram?> programLoader,
        Func<CommandCall, CommandResult> commandRunner,
        Action<string>? messageSink = null)
    {
        _programLoader = programLoader;
        _commandRunner = commandRunner;
        _messageSink = messageSink;
    }

    public CommandResult Run(ClProgram program, IReadOnlyList<string>? parameters = null)
    {
        var callDepth = 0;
        return RunCore(program, parameters, ref callDepth);
    }

    private CommandResult RunCore(ClProgram program, IReadOnlyList<string>? parameters, ref int callDepth)
    {
        if (++callDepth > 20)
        {
            return CommandResult.Error("Call depth exceeded.");
        }

        var symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
        {
            var index = 0;
            foreach (var entry in program.EntryParameters)
            {
                symbols[entry] = index < parameters.Count ? parameters[index] : string.Empty;
                index++;
            }
        }

        var pc = 0;
        while (pc < program.Statements.Count)
        {
            var statement = program.Statements[pc];

            switch (statement.Kind)
            {
                case ClStatementKind.Program:
                    pc++;
                    break;

                case ClStatementKind.Declare:
                    symbols[statement.VariableName!] = statement.Value ?? string.Empty;
                    pc++;
                    break;

                case ClStatementKind.Change:
                    symbols[statement.VariableName!] = Evaluate(statement.Value, symbols);
                    pc++;
                    break;

                case ClStatementKind.If:
                    pc = HandleIf(statement, program, symbols, pc);
                    break;

                case ClStatementKind.Else:
                    pc = statement.Jump;
                    break;

                case ClStatementKind.EndIf:
                    pc++;
                    break;

                case ClStatementKind.Goto:
                    if (!program.Labels.TryGetValue(statement.Label!, out var target))
                    {
                        return CommandResult.Error($"Label '{statement.Label}' not found.");
                    }

                    pc = target;
                    break;

                case ClStatementKind.Label:
                    pc++;
                    break;

                case ClStatementKind.Call:
                    var callResult = ExecuteCall(statement, symbols, ref callDepth);
                    if (callResult.Outcome != CommandOutcome.Continue)
                    {
                        return callResult;
                    }

                    pc++;
                    break;

                case ClStatementKind.SendProgramMessage:
                    _messageSink?.Invoke(Evaluate(statement.Value, symbols));
                    pc++;
                    break;

                case ClStatementKind.Command:
                    var commandResult = ExecuteCommand(statement.Command!, symbols);
                    if (commandResult.Outcome != CommandOutcome.Continue)
                    {
                        return commandResult;
                    }

                    pc++;
                    break;

                case ClStatementKind.EndProgram:
                    return CommandResult.Ok();

                default:
                    pc++;
                    break;
            }
        }

        return CommandResult.Ok();
    }

    private int HandleIf(
        ClStatement statement,
        ClProgram program,
        IReadOnlyDictionary<string, string> symbols,
        int pc)
    {
        var condition = EvaluateCondition(statement.Condition!, symbols);

        if (statement.Then is null)
        {
            return condition ? pc + 1 : statement.Jump;
        }

        if (!condition)
        {
            return pc + 1;
        }

        var inline = CommandParser.Parse(statement.Then);
        if (inline.Name.Equals("GOTO", StringComparison.OrdinalIgnoreCase))
        {
            var label = inline.GetOption("CMDLBL") ?? inline.Positional.FirstOrDefault();
            label = CommandParser.Unquote(label);
            if (label is null || !program.Labels.TryGetValue(label, out var target))
            {
                throw new ClRuntimeException($"IF..THEN(GOTO) label '{label}' not found.");
            }

            return target;
        }

        var result = _commandRunner(inline);
        if (result.Outcome != CommandOutcome.Continue)
        {
            throw new ClRuntimeException(result.Message ?? "Command failed.");
        }

        return pc + 1;
    }

    private CommandResult ExecuteCall(ClStatement statement, IReadOnlyDictionary<string, string> symbols, ref int callDepth)
    {
        var parts = SplitProgramTarget(statement.ProgramName);
        var library = parts.library ?? "*LIBL";
        var name = parts.name;

        var program = _programLoader(library, name);
        if (program is not null)
        {
            var parameters = statement.Parameters
                .Select(p => ResolveValue(p, symbols))
                .ToList();
            return RunCore(program, parameters, ref callDepth);
        }

        var fallback = new CommandCall
        {
            Name = "CALL",
            Positional = new[] { statement.ProgramName! },
            Keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PARM"] = string.Join(' ', statement.Parameters.Select(p => ResolveValue(p, symbols))),
            },
        };
        return _commandRunner(fallback);
    }

    public CommandResult ExecuteCommand(CommandCall call)
    {
        return _commandRunner(call);
    }

    private CommandResult ExecuteCommand(CommandCall call, IReadOnlyDictionary<string, string> symbols)
    {
        return _commandRunner(Substitute(call, symbols));
    }

    private static string Evaluate(string? expression, IReadOnlyDictionary<string, string> symbols)
    {
        if (expression is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in expression.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0)
            {
                continue;
            }

            builder.Append(ResolveValue(part, symbols));
        }

        return builder.ToString();
    }

    private static string ResolveValue(string? value, IReadOnlyDictionary<string, string> symbols)
    {
        if (value is null)
        {
            return string.Empty;
        }

        value = value.Trim();
        if (value.StartsWith('\'') && value.EndsWith('\''))
        {
            return CommandParser.Unquote(value);
        }

        if (value.StartsWith('&') && symbols.TryGetValue(value, out var resolved))
        {
            return resolved;
        }

        return value;
    }

    private static bool EvaluateCondition(string condition, IReadOnlyDictionary<string, string> symbols)
    {
        foreach (var op in new[] { "*EQ", "*NE", "*GT", "*LT", "*GE", "*LE" })
        {
            var index = condition.IndexOf(op, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var lhs = condition[..index].Trim();
            var rhs = condition[(index + op.Length)..].Trim();
            var comparison = string.Compare(ResolveValue(lhs, symbols), ResolveValue(rhs, symbols), StringComparison.Ordinal);
            return op switch
            {
                "*EQ" => comparison == 0,
                "*NE" => comparison != 0,
                "*GT" => comparison > 0,
                "*LT" => comparison < 0,
                "*GE" => comparison >= 0,
                "*LE" => comparison <= 0,
                _ => false,
            };
        }

        return false;
    }

    private static (string? library, string name) SplitProgramTarget(string? target)
    {
        if (target is null)
        {
            return (null, string.Empty);
        }

        var slash = target.IndexOf('/');
        return slash >= 0
            ? (target[..slash], target[(slash + 1)..])
            : (null, target);
    }

    private static CommandCall Substitute(CommandCall call, IReadOnlyDictionary<string, string> symbols)
    {
        return new CommandCall
        {
            Name = call.Name,
            Positional = call.Positional.Select(p => ResolveValue(p, symbols)).ToList(),
            Keywords = call.Keywords.ToDictionary(
                kv => kv.Key,
                kv => ResolveValue(kv.Value, symbols),
                StringComparer.OrdinalIgnoreCase),
        };
    }
}

public sealed class ClRuntimeException : Exception
{
    public ClRuntimeException(string? message) : base(message)
    {
    }
}