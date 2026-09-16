using Ipc.Cl.Parsing;
using System.Text;
using Ipc.Cl.Commands;

namespace Ipc.Cl.Interpreter;

public sealed class ClInterpreter
{
    private readonly Func<string, string, ClProgram?> _programLoader;
    private readonly Func<CommandCall, CommandResult> _commandRunner;
    private readonly Action<string>? _messageSink;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<ClProgram, IDisposable?>? _programScope;
    private readonly Func<int> _ccsid;
    private readonly Func<ClFileBinding, ClDatabaseCursor>? _openFile;
    private readonly Func<string, string, IReadOnlyList<Ipc.Core.Work.ProgramArgument>, CommandResult>? _externalCaller;
    private readonly Func<CommandCall, ClCommandContext, CommandResult?>? _contextCommandRunner;
    private readonly Func<ClStatement, object, ClCommandContext, ClProgramMessage>? _programMessageSender;
    private readonly Func<CommandResult, bool, CommandResult>? _failureReporter;
    private readonly Action<CommandResult>? _failureHandled;
    private readonly Func<Ipc.Core.Work.ProgramArgument>? _localDataArea;

    public ClInterpreter(
        Func<string, string, ClProgram?> programLoader,
        Func<CommandCall, CommandResult> commandRunner,
        Action<string>? messageSink = null,
        CancellationToken cancellationToken = default, Func<ClProgram, IDisposable?>? programScope = null, Func<int>? ccsid = null, Func<ClFileBinding, ClDatabaseCursor>? openFile = null,
        Func<string, string, IReadOnlyList<Ipc.Core.Work.ProgramArgument>, CommandResult>? externalCaller = null,
        Func<CommandCall, ClCommandContext, CommandResult?>? contextCommandRunner = null,
        Func<ClStatement, object, ClCommandContext, ClProgramMessage>? programMessageSender = null,
        Func<CommandResult, bool, CommandResult>? failureReporter = null, Action<CommandResult>? failureHandled = null,
        Func<Ipc.Core.Work.ProgramArgument>? localDataArea = null)
    {
        _programLoader = programLoader;
        _commandRunner = commandRunner;
        _messageSink = messageSink;
        _cancellationToken = cancellationToken;
        _programScope = programScope;
        _ccsid = ccsid ?? (() => 37);
        _openFile = openFile;
        _externalCaller = externalCaller;
        _contextCommandRunner = contextCommandRunner;
        _programMessageSender = programMessageSender;
        _failureReporter = failureReporter;
        _failureHandled = failureHandled;
        _localDataArea = localDataArea;
    }

    public CommandResult Run(ClProgram program, IReadOnlyList<string>? parameters = null) =>
        RunWithArguments(program, parameters?.Cast<object?>().ToArray()).Result;

    public ClInvocationResult RunWithArguments(ClProgram program, IReadOnlyList<object?>? parameters = null)
    {
        parameters ??= Array.Empty<object?>();
        if (parameters.Count != program.EntryParameters.Count || parameters.Count > 256)
            return new(CommandResult.Error("CL entry parameter count does not match PGM PARM."), Array.Empty<object?>());
        var byteCount = 0L;
        foreach (var parameter in parameters)
        {
            if (parameter is not (null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Ipc.Core.Work.ProgramBuffer or Ipc.Core.Work.ProgramConstant))
                return new(CommandResult.Error("Unsupported CL argument value."), Array.Empty<object?>());
            byteCount += parameter is Ipc.Core.Work.ProgramConstant constant ? constant.Buffer.Length : parameter is Ipc.Core.Work.ProgramBuffer buffer ? buffer.Length : Encoding.UTF8.GetByteCount(ClExpression.Text(parameter ?? ""));
            if (byteCount > 1048576) return new(CommandResult.Error("CL arguments exceed 1 MiB."), Array.Empty<object?>());
        }
        var cells = new List<ClVariableCell>();
        try
        {
            var declarations = program.Statements.Where(s => s.Kind == ClStatementKind.Declare).ToDictionary(s => s.VariableName!, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < parameters.Count; i++)
            {
                var definition = declarations.TryGetValue(program.EntryParameters[i], out var declaration) ? ClVariableDefinition.From(declaration, _ccsid()) : null;
                var value = parameters[i] ?? "";
                if (value is Ipc.Core.Work.ProgramConstant constant) { cells.Add(ClCallArgument.Cell(constant, definition)); continue; }
                if (definition is null && value is Ipc.Core.Work.ProgramBuffer raw) definition = new ClVariableDefinition("*CHAR", raw.Length, 0);
                if (definition?.Type == "*CHAR" && value is Ipc.Core.Work.ProgramBuffer characterBuffer)
                {
                    var cell = new ClVariableCell("", definition, _ccsid()); cell.AssignBuffer(characterBuffer); cells.Add(cell); continue;
                }
                var text = value is Ipc.Core.Work.ProgramBuffer buffer
                    ? definition?.Decode(buffer) ?? buffer.ToText()
                    : definition?.Assign(value, _ccsid()) ?? ClExpression.Text(value);
                cells.Add(new(text, definition, _ccsid()));
            }
            var callDepth = 0;
            var result = RunCore(program, cells, ref callDepth);
            return new(result, cells.Select((cell, index) => parameters[index] is Ipc.Core.Work.ProgramBuffer
                ? (object?)(cell.ToBuffer() ?? parameters[index])
                : cell.ResultValue()).ToArray());
        }
        catch (ClRuntimeException error) { return new(CommandResult.Error(error.Message, error.MessageId), Array.Empty<object?>()); }
        catch (EncoderFallbackException) { return new(CommandResult.Error("Parameter is not representable in the job CCSID."), Array.Empty<object?>()); }
        catch (Ipc.Core.Messages.CpfException error) { return new(CommandResult.Error(error.Message), Array.Empty<object?>()); }
    }

    private CommandResult RunCore(ClProgram program, IReadOnlyList<ClVariableCell> parameters, ref int callDepth)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (callDepth >= 20)
        {
            return CommandResult.Error("Call depth exceeded.");
        }
        using var scope = _programScope?.Invoke(program);
        callDepth++;
        try
        {
            var result = RunBody(program, parameters, ref callDepth);
            return result.IsError ? ReportFailure(result, outgoing: true) : result;
        }
        finally { callDepth--; }
    }

    private CommandResult RunBody(ClProgram program, IReadOnlyList<ClVariableCell> parameters, ref int callDepth)
    {
        if (parameters.Count != program.EntryParameters.Count) return CommandResult.Error("CL entry parameter count does not match PGM PARM.");
        foreach (var monitor in program.Monitors.Concat(program.Statements.SelectMany(s => s.Monitors))) monitor.Validate(_ccsid());
        using var files = new ClOpenFiles(program.Files, _openFile);
        var symbols = new ClVariables();
        for (var index = 0; index < parameters.Count; index++) symbols.Bind(program.EntryParameters[index], parameters[index]);

        var definitions = program.Statements.Where(s => s.Kind == ClStatementKind.Declare)
            .ToDictionary(s => s.VariableName!, s => ClVariableDefinition.From(s, _ccsid()), StringComparer.OrdinalIgnoreCase);
        object Read(string name)
        {
            if (name == "*LDA") return _localDataArea?.Invoke().ToBuffer() ?? throw new ClRuntimeException("The local data area requires a job host.");
            if (!symbols.ContainsKey(name)) throw new ClRuntimeException($"Variable '{name}' is not initialized.");
            if (symbols.Cell(name).RawBuffer is { } raw) return raw;
            var value = symbols[name];
            return definitions.TryGetValue(name, out var definition) && definition is not null ? definition.Read(value) : value;
        }
        object EvaluateExpression(ClExpression expression) => expression.Evaluate(Read, _ccsid());
        void Assign(string name, object value)
        {
            if (name == "*LDA")
            {
                var area = _localDataArea?.Invoke() ?? throw new ClRuntimeException("The local data area requires a job host.");
                area.Value = value; return;
            }
            var definition = definitions.GetValueOrDefault(name) ?? (symbols.ContainsKey(name) ? symbols.Cell(name).Definition : null);
            if (definition?.Type == "*CHAR" && value is Ipc.Core.Work.ProgramBuffer buffer)
            {
                var bytes = Enumerable.Repeat(Ipc.Core.Text.CodePage.FromCcsid(_ccsid()).GetBytes(" ")[0], definition.Length).ToArray();
                buffer.ToArray().AsSpan(0, Math.Min(buffer.Length, bytes.Length)).CopyTo(bytes);
                symbols.Cell(name).AssignBuffer(new(bytes, _ccsid()));
            }
            else symbols[name] = definition?.Assign(value, _ccsid()) ?? ClExpression.Text(value);
        }
        bool ForWithinRange(ClStatement head)
        {
            var limit = ClExpression.Number(EvaluateExpression(head.TerminalExpression!));
            if (decimal.Truncate(limit) != limit) throw new ClRuntimeException("DOFOR TO requires an integer value.");
            var counter = ClExpression.Number(Read(head.VariableName!));
            return head.Increment >= 0 ? counter <= limit : counter >= limit;
        }
        var pc = 0;
        bool HandleFailure(ref CommandResult error, ClStatement statement)
        {
            error = ReportFailure(error, outgoing: false);
            var delivered = error;
            var monitor = statement.Monitors.Concat(program.Monitors).FirstOrDefault(m => m.Matches(delivered, _ccsid()));
            if (monitor is null) return false;
            _failureHandled?.Invoke(error);
            pc = monitor.Handler >= 0 ? monitor.Handler : statement.Kind switch {
                ClStatementKind.If or ClStatementKind.ForBegin => statement.Jump,
                ClStatementKind.ForEnd => program.Statements[statement.LoopHead].Jump,
                _ => pc + 1 };
            return true;
        }
        while (pc < program.Statements.Count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Ipc.Core.Work.JobExecutionBudget.Checkpoint();
            var statement = program.Statements[pc];

            try
            {
            switch (statement.Kind)
            {
                case ClStatementKind.Program:
                    pc++;
                    break;

                case ClStatementKind.Declare:
                    var definition = definitions[statement.VariableName!];
                    if (!symbols.ContainsKey(statement.VariableName!))
                    {
                        var initialValue = statement.Expression is { } initial ? EvaluateExpression(initial) : definition?.Default ?? statement.Value ?? string.Empty;
                        if (initialValue is Ipc.Core.Work.ProgramBuffer && definition?.Type == "*CHAR")
                        {
                            symbols.Add(statement.VariableName!, definition.Default, definition, _ccsid()); Assign(statement.VariableName!, initialValue);
                        }
                        else symbols.Add(statement.VariableName!, definition?.Assign(initialValue, _ccsid()) ?? ClExpression.Text(initialValue), definition, _ccsid());
                    }
                    else if (symbols.Cell(statement.VariableName!).Definition is { } actual && definition is not null && actual != definition)
                        throw new ClRuntimeException("CL reference parameter type and length must match its declaration.");
                    pc++;
                    break;

                case ClStatementKind.Change:
                    var assigned = statement.Expression is { } expression ? EvaluateExpression(expression) : Evaluate(statement.Value, symbols);
                    if (statement.TargetExpression is { } storage) storage.AssignStorage(assigned, Read, Assign, _ccsid());
                    else Assign(statement.VariableName!, assigned);
                    pc++;
                    break;

                case ClStatementKind.If:
                    pc = statement.Expression is { } condition ? ClExpression.Logical(EvaluateExpression(condition)) ? pc + 1 : statement.Jump
                        : HandleIf(statement, program, symbols, pc);
                    break;

                case ClStatementKind.ReceiveFile:
                    var received = files.Receive(statement.OpenId!);
                    var binding = program.Files.Single(f => f.Request.OpenId == statement.OpenId);
                    foreach (var field in binding.Definition.Fields)
                    {
                        if (!received.Values.TryGetValue(field.Name, out var value)) throw new ClRuntimeException("File host omitted a declared field.");
                        Assign("&" + (statement.OpenId == "*NONE" ? "" : statement.OpenId + "_") + field.Name, value ?? "");
                    }
                    if (received.ErrorId is { } errorId) throw new ClRuntimeException(received.Error ?? "File record could not be fully converted.", errorId);
                    pc++;
                    break;

                case ClStatementKind.CloseFile:
                    files.Close(statement.OpenId!); pc++; break;

                case ClStatementKind.ForBegin:
                    Assign(statement.VariableName!, ClExpression.Number(EvaluateExpression(statement.Expression!)));
                    pc = ForWithinRange(statement) ? pc + 1 : statement.Jump;
                    break;

                case ClStatementKind.ForEnd:
                    var head = program.Statements[statement.LoopHead];
                    Assign(head.VariableName!, checked(ClExpression.Number(Read(head.VariableName!)) + head.Increment));
                    pc = ForWithinRange(head) ? statement.LoopHead + 1 : head.Jump;
                    break;

                case ClStatementKind.Branch:
                case ClStatementKind.Else:
                    pc = statement.Jump;
                    break;

                case ClStatementKind.EndIf:
                    pc++;
                    break;

                case ClStatementKind.Goto:
                    if (!program.Labels.TryGetValue(statement.Label!, out var target))
                    {
                        var missing = CommandResult.Error($"Label '{statement.Label}' not found.");
                        if (HandleFailure(ref missing, statement)) break;
                        return Locate(missing, statement);
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
                        if (callResult.IsError && HandleFailure(ref callResult, statement)) break;
                        return Locate(callResult, statement);
                    }

                    pc++;
                    break;

                case ClStatementKind.SendProgramMessage:
                    var messageValue = statement.Expression is { } message ? EvaluateExpression(message) : Evaluate(statement.Value, symbols);
                    ClProgramMessage prepared;
                    if (_programMessageSender is not null)
                        prepared = _programMessageSender(statement, messageValue, new(name => symbols.Cell(name).Borrow(), value => ClCommandArguments.Resolve(value, name => symbols[name])));
                    else
                    {
                        if (statement.MessageId is not (null or "CPF9898") || statement.MessageType == "*INQ" || statement.Command?.GetOption("KEYVAR") is not null || statement.Command?.GetOption("TOMSGQ") is not null || statement.Command?.GetOption("TOPGMQ") is { } messageTarget && messageTarget != "*PRV")
                            throw new ClRuntimeException("Program message descriptions and queues require a message host.");
                        prepared = new(ClExpression.Text(messageValue), statement.MessageId, messageValue as Ipc.Core.Work.ProgramBuffer);
                    }
                    if (statement.MessageType == "*ESCAPE")
                        return Locate(CommandResult.Error(prepared.Text, prepared.MessageId, prepared.Text, prepared.Reference, prepared.ReplacementData), statement);
                    _messageSink?.Invoke(prepared.Text);
                    pc++;
                    break;

                case ClStatementKind.Command:
                    var commandResult = ExecuteCommand(statement.Command!, symbols);
                    if (commandResult.Outcome != CommandOutcome.Continue)
                    {
                        if (commandResult.IsError && HandleFailure(ref commandResult, statement)) break;
                        return Locate(commandResult, statement);
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
            catch (ClRuntimeException error)
            {
                var failure = CommandResult.Error(error.Message, error.MessageId);
                if (!HandleFailure(ref failure, statement)) return Locate(failure, statement);
            }
            catch (Ipc.Core.Messages.CpfException error)
            {
                var failure = CommandResult.Error(error.Message, error.MessageId);
                if (!HandleFailure(ref failure, statement)) return Locate(failure, statement);
            }
            catch (OverflowException)
            {
                var failure = CommandResult.Error("Arithmetic overflow.", "MCH1210");
                if (!HandleFailure(ref failure, statement)) return Locate(failure, statement);
            }
            catch (EncoderFallbackException)
            {
                var failure = CommandResult.Error("Character value is not representable in the job CCSID.");
                if (!HandleFailure(ref failure, statement)) return Locate(failure, statement);
            }
        }

        return CommandResult.Ok();
    }

    private static CommandResult Locate(CommandResult result, ClStatement statement) => result.IsError && statement.Location is { } location
        ? CommandResult.Error($"{location}: {result.Message}", result.MessageId, result.MessageData, result.ExceptionReference, result.MessageDataBuffer) : result;

    private CommandResult ReportFailure(CommandResult failure, bool outgoing)
    {
        try { return _failureReporter?.Invoke(failure, outgoing) ?? failure; }
        catch (Ipc.Core.Messages.CpfException error)
        {
            // A full/revoked queue must not recursively attempt to report its own
            // reporting failure. Propagate the delivery error through normal MONMSG.
            return CommandResult.Error("Cannot deliver " + failure.MessageId + ": " + error.Message, error.MessageId);
        }
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

    private CommandResult ExecuteCall(ClStatement statement, ClVariables symbols, ref int callDepth)
    {
        var parts = SplitProgramTarget(ClCommandArguments.Resolve(statement.ProgramName ?? "", name => symbols[name]).TrimEnd());
        var library = parts.library ?? "*LIBL";
        var name = parts.name;
        long argumentBytes = 0;
        void Account(int size)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            argumentBytes += size;
            if (argumentBytes > 1048576) throw new ClRuntimeException("CALL arguments exceed 1 MiB.");
        }

        var program = _programLoader(library, name);
        if (program is not null)
        {
            if (statement.Parameters.Count != program.EntryParameters.Count) return CommandResult.Error("CL entry parameter count does not match PGM PARM.");
            var declarations = program.Statements.Where(s => s.Kind == ClStatementKind.Declare).ToDictionary(s => s.VariableName!, StringComparer.OrdinalIgnoreCase);
            var parameters = new List<ClVariableCell>();
            for (var index = 0; index < statement.Parameters.Count; index++)
            {
                var argument = statement.Parameters[index].Trim();
                var expected = declarations.TryGetValue(program.EntryParameters[index], out var declaration) ? ClVariableDefinition.From(declaration, _ccsid()) : null;
                var parsed = statement.CallArguments.Count == statement.Parameters.Count ? statement.CallArguments[index] : ClCallArgument.Compile(argument);
                if (parsed.ReferenceName is { } variable)
                {
                    var cell = symbols.Cell(variable);
                    Account(cell.Definition?.StorageLength ?? Encoding.UTF8.GetByteCount(cell.Value));
                    if (cell.Definition is { } actual && expected is not null && actual != expected)
                        throw new ClRuntimeException("CL reference parameter type and length must match its declaration.");
                    if (expected is not null && cell.Definition is null) _ = expected.Assign(cell.Value, _ccsid());
                    parameters.Add(cell);
                }
                else
                {
                    var value = parsed.Evaluate(name => ReadCallValue(symbols, name), _ccsid());
                    Account(value.Buffer.Length);
                    parameters.Add(ClCallArgument.Cell(value, expected));
                }
            }
            return RunCore(program, parameters, ref callDepth);
        }

        if (_externalCaller is not null)
        {
            var cells = new Dictionary<ClVariableCell, Ipc.Core.Work.ProgramArgument>();
            var arguments = new List<Ipc.Core.Work.ProgramArgument>();
            for (var index = 0; index < statement.Parameters.Count; index++)
            {
                var argument = statement.Parameters[index].Trim();
                var parsed = statement.CallArguments.Count == statement.Parameters.Count ? statement.CallArguments[index] : ClCallArgument.Compile(argument);
                if (parsed.ReferenceName is not { } variable)
                {
                    var temporary = parsed.Evaluate(name => ReadCallValue(symbols, name), _ccsid());
                    Account(temporary.Buffer.Length); arguments.Add(ClCallArgument.Temporary(temporary)); continue;
                }
                var cell = symbols.Cell(variable);
                Account(cell.Definition?.StorageLength ?? Encoding.UTF8.GetByteCount(cell.Value));
                if (!cells.TryGetValue(cell, out var reference))
                {
                    reference = cell.Borrow();
                    cells.Add(cell, reference);
                }
                arguments.Add(reference);
            }
            return _externalCaller(library, name, arguments);
        }

        var fallback = new CommandCall
        {
            Name = "CALL",
            Positional = new[] { library + "/" + name },
            Keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PARM"] = string.Join(' ', statement.Parameters.Select(p => "\'" + ResolveValue(p, symbols).Replace("\'", "\'\'", StringComparison.Ordinal) + "\'")),
            },
        };
        return _commandRunner(fallback);
    }

    private static object CallValue(ClVariableCell cell) => (object?)cell.RawBuffer ?? cell.Definition?.Read(cell.Value) ?? cell.Value;
    private object ReadCallValue(ClVariables symbols, string name) => name == "*LDA"
        ? _localDataArea?.Invoke().ToBuffer() ?? throw new ClRuntimeException("*LDA requires a job host.") : CallValue(symbols.Cell(name));

    public CommandResult ExecuteCommand(CommandCall call)
    {
        return _commandRunner(call);
    }

    private CommandResult ExecuteCommand(CommandCall call, ClVariables symbols)
    {
        if (_contextCommandRunner?.Invoke(call, new(name => symbols.Cell(name).Borrow(), value => ClCommandArguments.Resolve(value, name => symbols[name]))) is { } result) return result;
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
        var expanded = new CommandCall
        {
            Name = call.Name,
            Positional = call.Positional.Select(p => ClCommandArguments.Render(p, name => symbols[name])).ToList(),
            Keywords = call.Keywords.ToDictionary(
                kv => kv.Key,
                kv => ClCommandArguments.Render(kv.Value, name => symbols[name]),
                StringComparer.OrdinalIgnoreCase),
        };
        if (expanded.ToString().Length > 32768) throw new ClRuntimeException("Expanded command exceeds 32768 characters.");
        return expanded;
    }
}

public sealed class ClRuntimeException : Exception
{
    public string MessageId { get; }
    public ClRuntimeException(string? message, string messageId = "IPC0006") : base(message) { MessageId = messageId; }
}
