using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Cl.Parsing;
using Ipc.Core.Compilation;

namespace Ipc.Cl.Interpreter;

/// <summary>Lowers structured CL commands to a flat, bounded instruction stream.</summary>
internal sealed class ClControlFlowCompiler(PreprocessedSource source, CancellationToken cancellationToken, Func<ClFileRequest, ClDatabaseFile>? fileResolver)
{
    private sealed record Line(string Text, SourceLocation Location);
    private sealed class Loop(string kind, IReadOnlyList<string> labels)
    {
        internal string Kind { get; } = kind;
        internal IReadOnlyList<string> Labels { get; } = labels;
        internal List<int> Breaks { get; } = new();
        internal List<int> Continues { get; } = new();
    }
    private readonly List<ClStatement> _statements = new();
    private readonly List<Loop> _loops = new();
    private readonly List<string> _labels = new();
    private List<Line> _lines = new();
    private int _position, _depth, _monitorCount;
    private int? _lastMonitorTarget;
    private bool _declarations = true;
    internal List<ClMessageMonitor> Monitors { get; } = new();
    internal List<ClFileBinding> Files { get; } = new();
    internal IReadOnlyList<ClStatement> Compile()
    {
        _lines = Lines(); Sequence(Array.Empty<string>());
        var declarations = new Dictionary<string, ClStatement>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _statements.Count; index++)
        {
            var declaration = _statements[index]; if (declaration.Kind != ClStatementKind.Declare) continue;
            if (declaration.VariableName is null || !Regex.IsMatch(declaration.VariableName, declaration.FileVariable ? @"\A&[A-Z][A-Z0-9_]{0,20}\z" : @"\A&[A-Z][A-Z0-9_]{0,9}\z")) throw Error(declaration.Location!, "Invalid CL variable name.");
            if (!declarations.TryAdd(declaration.VariableName, declaration))
            {
                var prior = declarations[declaration.VariableName];
                if (!(prior.FileVariable || declaration.FileVariable) || prior.Value is not null || declaration.Value is not null ||
                    !SameDeclaration(prior, declaration))
                    throw Error(declaration.Location!, "Duplicate or incompatible CL declaration.");
                _statements[index] = new() { Kind = ClStatementKind.NoOp, Location = declaration.Location }; continue;
            }
            try { _ = ClVariableDefinition.From(declaration); } catch (ClRuntimeException error) { throw Error(declaration.Location!, error.Message); }
        }
        foreach (var loop in _statements.Where(s => s.Kind == ClStatementKind.ForBegin))
            if (!declarations.TryGetValue(loop.VariableName!, out var control) || control.DeclarationType is not ("*INT" or "*UINT"))
                throw Error(loop.Location!, "DOFOR requires a declared *INT or *UINT control variable.");
        var programHeaders = _statements.Where(s => s.Kind == ClStatementKind.Program).ToArray();
        if (programHeaders.Length > 1) throw Error(programHeaders[1].Location!, "Only one PGM header is allowed.");
        foreach (var program in programHeaders)
        {
            if (program.EntryParameters.Count > 256 || program.EntryParameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != program.EntryParameters.Count ||
                program.EntryParameters.Any(p => !Regex.IsMatch(p, declarations.TryGetValue(p, out var variable) && variable.FileVariable ? @"\A&[A-Za-z][A-Za-z0-9_]{0,20}\z" : @"\A&[A-Za-z][A-Za-z0-9_]{0,9}\z"))) throw Error(program.Location!, "Invalid or duplicate PGM entry parameter.");
        }
        foreach (var change in _statements.Where(s => s.Kind == ClStatementKind.Change))
            if (change.TargetExpression?.IsStorageTarget != true && (change.VariableName is null || !Regex.IsMatch(change.VariableName, @"\A&[A-Z][A-Z0-9_]{0,20}\z"))) throw Error(change.Location!, "CHGVAR requires a variable or byte-function target.");
        foreach (var statement in _statements)
            foreach (var expression in new[] { statement.Expression, statement.TerminalExpression, statement.TargetExpression })
                foreach (var name in expression?.CharacterStorageVariables ?? Array.Empty<string>())
                    if (name != "*LDA" && (!declarations.TryGetValue(name, out var declared) || declared.DeclarationType != "*CHAR"))
                        throw Error(statement.Location!, "Byte functions require a declared CHAR variable.");
        return _statements;
    }
    private static bool SameDeclaration(ClStatement first, ClStatement second)
    {
        try { return ClVariableDefinition.From(first) == ClVariableDefinition.From(second); }
        catch (ClRuntimeException) { return false; }
    }
    private List<Line> Lines()
    {
        var lines = new List<Line>(); var physical = source.Text.Split('\n'); string? pending = null; SourceLocation? start = null; char continuation = '\0';
        for (var index = 0; index < physical.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = physical[index].TrimEnd();
            if (text.TrimStart().StartsWith('*') || text.TrimStart().StartsWith("//", StringComparison.Ordinal) || text.Length == 0) continue;
            var location = index < source.Locations.Count ? source.Locations[index] : new SourceLocation("*SOURCE", index + 1, 1, Array.Empty<string>());
            var next = text[^1] is '+' or '-' ? text[^1] : '\0'; if (next != '\0') text = text[..^1];
            if (pending is null) { start = location; pending = text.TrimStart(); }
            else pending += continuation == '+' ? text.TrimStart() : text;
            if (pending.Length > 32768) throw Error(start!, "Continued CL command exceeds 32768 characters.");
            continuation = next;
            if (next != '\0') continue;
            lines.Add(new(pending, start!)); pending = null;
        }
        if (pending is not null) throw Error(start!, "Unfinished CL command continuation.");
        return lines;
    }
    private void Sequence(IReadOnlyList<string> ends)
    {
        while (_position < _lines.Count && !ends.Contains(Name(_lines[_position].Text)))
        {
            var line = _lines[_position++]; Command(line);
        }
    }
    private void Command(Line line)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++_depth > 64) throw Error(line.Location, "CL control nesting exceeds 64.");
        try
        {
            var label = Regex.Match(line.Text, @"\A([A-Za-z][A-Za-z0-9_]{0,9}):\s*(.*)\z", RegexOptions.CultureInvariant);
            if (label.Success)
            {
                var name = label.Groups[1].Value.ToUpperInvariant(); Emit(new() { Kind = ClStatementKind.Label, Label = name }, line.Location); _labels.Add(name);
                if (label.Groups[2].Length > 0) Command(new(label.Groups[2].Value, line.Location with { Column = line.Location.Column + label.Groups[2].Index }));
                return;
            }
            var labels = _labels.ToArray(); _labels.Clear();
            var call = CommandParser.Parse(line.Text);
            var commandName = call.Name.ToUpperInvariant();
            if (commandName != "MONMSG")
            {
                _lastMonitorTarget = null;
                if (commandName is not ("PGM" or "DCL" or "DCLF")) _declarations = false;
            }
            switch (commandName)
            {
                case "IF": If(call, line); _lastMonitorTarget = null; break;
                case "DO": case "DOWHILE": case "DOUNTIL": case "DOFOR": Group(call, line, labels); _lastMonitorTarget = null; break;
                case "SELECT": Select(call, line); _lastMonitorTarget = null; break;
                case "MONMSG": Monitor(call, line); break;
                case "DCLF": DeclareFile(call, line); break;
                case "RCVF": case "CLOSE": _lastMonitorTarget = FileOperation(call, line); break;
                case "SNDPGMMSG": _lastMonitorTarget = Emit(SendProgramMessage(call, line), line.Location); break;
                case "LEAVE": case "ITERATE":
                    Check(call, "CMDLBL", 1, line); var target = Parameter(call, "CMDLBL", 0)?.ToUpperInvariant(); if (target == "*NONE") target = null;
                    var loop = _loops.LastOrDefault(l => l.Kind != "DO" && (target is null || l.Labels.Contains(target)));
                    if (loop is null) throw Error(line.Location, call.Name + " requires an enclosing iterative group with the requested label.");
                    (call.Name.Equals("LEAVE", StringComparison.OrdinalIgnoreCase) ? loop.Breaks : loop.Continues).Add(Emit(new() { Kind = ClStatementKind.Branch }, line.Location)); break;
                case "RETURN": Check(call, "", 0, line); Emit(new() { Kind = ClStatementKind.EndProgram }, line.Location); break;
                case "PGM":
                    Check(call, "PARM", 1, line); Emit(new() { Kind = ClStatementKind.Program, EntryParameters = call.Split("PARM").Count > 0 ? call.Split("PARM") : CommandParser.Tokenize(Parameter(call, "PARM", 0) ?? "") }, line.Location); break;
                case "ENDPGM": Check(call, "", 0, line); Emit(new() { Kind = ClStatementKind.EndProgram }, line.Location); break;
                case "ENDDO": case "ELSE": case "ENDIF": case "WHEN": case "OTHERWISE": case "ENDSELECT": case "ENDSL":
                    throw Error(line.Location, "Unmatched " + call.Name + ".");
                default:
                    if (call.Name.Equals("DCL", StringComparison.OrdinalIgnoreCase)) Check(call, "VAR TYPE LEN VALUE", 4, line);
                    if (call.Name.Equals("CHGVAR", StringComparison.OrdinalIgnoreCase)) Check(call, "VAR VALUE", 2, line);
                    if (call.Name.Equals("CALL", StringComparison.OrdinalIgnoreCase))
                    {
                        Check(call, "PGM PARM", 1, line);
                        if (string.IsNullOrWhiteSpace(Parameter(call, "PGM", 0))) throw Error(line.Location, "CALL requires a program target.");
                        if (call.Split("PARM").Count > 256) throw Error(line.Location, "CALL supports at most 256 parameters.");
                    }
                    var statement = ClCompiler.ParseStatement(line.Text);
                    if (statement.Kind == ClStatementKind.Change && statement.VariableName?.StartsWith('%') == true)
                        statement.TargetExpression = Expression(statement.VariableName, line);
                    if (statement.Kind is ClStatementKind.Change or ClStatementKind.SendProgramMessage)
                        statement.Expression = Expression(call.GetOption(statement.Kind == ClStatementKind.Change ? "VALUE" : "MSG") ?? Parameter(call, statement.Kind == ClStatementKind.Change ? "VALUE" : "MSG", statement.Kind == ClStatementKind.Change ? 1 : 0) ?? "''", line);
                    if (statement.Kind == ClStatementKind.Declare && statement.Value is { } initial) statement.Expression = Expression(initial, line);
                    var emitted = Emit(statement, line.Location);
                    _lastMonitorTarget = statement.Kind is ClStatementKind.Command or ClStatementKind.Change or ClStatementKind.Call or ClStatementKind.SendProgramMessage ? emitted : null;
                    break;
            }
        }
        catch (Ipc.Core.Messages.CpfException error) { throw Error(line.Location, error.Message); }
        catch (InvalidDataException error) { throw Error(line.Location, error.Message); }
        catch (ClParseException error) { throw Error(line.Location, error.Message); }
        catch (ClRuntimeException error) { throw Error(line.Location, error.Message); }
        finally { _depth--; }
    }
    private void DeclareFile(CommandCall call, Line line)
    {
        Check(call, "FILE RCDFMT OPNID ALWVARLEN ALWNULL ALWGRAPHIC DCLBINFLD", 2, line);
        if (!_declarations || Files.Count >= 5) throw Error(line.Location, "DCLF requires the declaration section and at most five files.");
        string Option(string keyword, string fallback) => CommandParser.Unquote(call.GetOption(keyword) ?? fallback).ToUpperInvariant();
        bool Flag(string keyword) => Option(keyword, "*NO") switch { "*YES" => true, "*NO" => false, _ => throw Error(line.Location, "Invalid " + keyword + ".") };
        var file = CommandParser.Unquote(Parameter(call, "FILE", 0)).ToUpperInvariant();
        var format = CommandParser.Unquote(Parameter(call, "RCDFMT", 1) ?? "*ALL").ToUpperInvariant();
        var id = Option("OPNID", "*NONE");
        if (file.Length is < 1 or > 22 || file.StartsWith('&') || format != "*ALL" && !Ipc.Core.Objects.ObjectName.IsValid(format) ||
            id != "*NONE" && !Ipc.Core.Objects.ObjectName.IsValid(id) || Files.Any(f => f.Request.OpenId == id)) throw Error(line.Location, "Invalid or repeated DCLF file, format or open identifier.");
        if (Flag("ALWGRAPHIC")) throw Error(line.Location, "Graphic database fields are not implemented.");
        var binary = Option("DCLBINFLD", "*DEC") switch { "*INT" => true, "*DEC" => false, _ => throw Error(line.Location, "Invalid DCLBINFLD.") };
        var request = new ClFileRequest(file, format, id, Flag("ALWVARLEN"), Flag("ALWNULL"), binary);
        var definition = fileResolver?.Invoke(request) ?? throw Error(line.Location, "DCLF requires a compile-time database file resolver.");
        ClFileBindings.Validate(definition);
        if (format != "*ALL" && format != definition.RecordFormat) throw Error(line.Location, "DCLF record format not found.");
        Files.Add(new(request, definition));
        foreach (var field in definition.Fields)
            Emit(new() { Kind = ClStatementKind.Declare, FileVariable = true, VariableName = "&" + (id == "*NONE" ? "" : id + "_") + field.Name,
                DeclarationType = field.Type, DeclarationLength = field.Length.ToString(CultureInfo.InvariantCulture) + (field.Type == "*DEC" ? " " + field.Decimals.ToString(CultureInfo.InvariantCulture) : "") }, line.Location);
    }
    private int FileOperation(CommandCall call, Line line)
    {
        var close = call.Name.Equals("CLOSE", StringComparison.OrdinalIgnoreCase);
        Check(call, close ? "OPNID" : "DEV RCDFMT OPNID WAIT", close ? 0 : 2, line);
        var id = CommandParser.Unquote(call.GetOption("OPNID") ?? "*NONE").ToUpperInvariant();
        var binding = Files.SingleOrDefault(f => f.Request.OpenId == id) ?? throw Error(line.Location, "File operation requires a preceding DCLF with the same OPNID.");
        var format = Parameter(call, "RCDFMT", 1)?.ToUpperInvariant() ?? "*FILE";
        if (!close && ((Parameter(call, "DEV", 0)?.ToUpperInvariant() ?? "*FILE") != "*FILE" ||
            (call.GetOption("WAIT")?.ToUpperInvariant() ?? "*YES") != "*YES" || format != "*FILE" && format != binding.Definition.RecordFormat))
            throw Error(line.Location, "Unsupported database RCVF device, wait or record format.");
        return Emit(new() { Kind = close ? ClStatementKind.CloseFile : ClStatementKind.ReceiveFile, OpenId = id }, line.Location);
    }
    private void Monitor(CommandCall call, Line line)
    {
        Check(call, "MSGID CMPDTA EXEC", 3, line);
        var target = _lastMonitorTarget; var programLevel = _declarations;
        if (!programLevel && target is null) throw Error(line.Location, "MONMSG must follow declarations or a monitorable command.");
        var monitors = programLevel ? Monitors : _statements[target!.Value].Monitors;
        if (++_monitorCount > 1000 || monitors.Count >= 100) throw Error(line.Location, "MONMSG exceeds 100 per scope or 1000 per program.");
        var ids = CommandParser.Tokenize(Parameter(call, "MSGID", 0) ?? "").Select(s => s.ToUpperInvariant()).ToArray();
        if (ids.Length is < 1 or > 50 || ids.Any(id => !Regex.IsMatch(id, @"\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}\z") || id.StartsWith("MCH", StringComparison.Ordinal) && id[3..].Any(c => !char.IsAsciiDigit(c))))
            throw Error(line.Location, "MONMSG requires 1 to 50 constant message identifiers.");
        var rawComparison = Parameter(call, "CMPDTA", 1);
        var comparison = rawComparison is null || rawComparison.Equals("*NONE", StringComparison.OrdinalIgnoreCase) ? null : CommandParser.Unquote(rawComparison);
        if (comparison is not null && rawComparison?.StartsWith('\'') == false) comparison = comparison.ToUpperInvariant();
        if (comparison?.Length > 28 || rawComparison?.StartsWith('&') == true) throw Error(line.Location, "MONMSG CMPDTA requires at most 28 constant bytes.");
        var action = Parameter(call, "EXEC", 2); var handler = -1;
        if (action is not null)
        {
            if (programLevel && !Name(action).Equals("GOTO", StringComparison.OrdinalIgnoreCase)) throw Error(line.Location, "Program-level MONMSG EXEC supports only GOTO.");
            var skip = Emit(new() { Kind = ClStatementKind.Branch }, line.Location);
            handler = _statements.Count;
            Command(new(action, line.Location));
            _statements[skip].Jump = _statements.Count;
        }
        monitors.Add(new(ids, comparison, handler));
        _lastMonitorTarget = target; _declarations = programLevel;
    }
    private static ClStatement SendProgramMessage(CommandCall call, Line line)
    {
        Check(call, "MSG MSGID MSGF MSGDTA MSGTYPE TOPGMQ TOMSGQ RPYMSGQ KEYVAR", 1, line);
        var type = call.GetOption("MSGTYPE")?.ToUpperInvariant() ?? "*INFO";
        var id = CommandParser.Unquote(call.GetOption("MSGID")).ToUpperInvariant();
        var file = CommandParser.Unquote(call.GetOption("MSGF")).ToUpperInvariant();
        if (type is not ("*INFO" or "*COMP" or "*DIAG" or "*ESCAPE" or "*INQ")) throw Error(line.Location, "This CL checkpoint does not support the requested message type.");
        if (id.Length > 0 && (id != "CPF9898" || file is not ("QCPFMSG" or "QSYS/QCPFMSG"))) throw Error(line.Location, "Only predefined QCPFMSG CPF9898 is available in this CL checkpoint.");
        if (type == "*ESCAPE" && id.Length == 0) throw Error(line.Location, "Escape messages require MSGID and MSGF.");
        if (call.GetOption("TOPGMQ") is { } queue && queue.ToUpperInvariant() is not ("*PRV" or "*SAME" or "*EXT")) throw Error(line.Location, "TOPGMQ supports *PRV, *SAME and *EXT.");
        if (type == "*ESCAPE" && (call.GetOption("TOPGMQ")?.ToUpperInvariant() is not (null or "*PRV") || call.GetOption("TOMSGQ") is not null)) throw Error(line.Location, "Escape propagation currently requires TOPGMQ(*PRV).");
        var immediate = Parameter(call, "MSG", 0);
        if (id.Length > 0 && immediate is not null || id.Length == 0 && (call.GetOption("MSGDTA") is not null || file.Length > 0)) throw Error(line.Location, "Immediate MSG and predefined MSGID/MSGF/MSGDTA cannot be combined.");
        var value = id.Length > 0 ? call.GetOption("MSGDTA") ?? "''" : immediate ?? throw Error(line.Location, "SNDPGMMSG requires MSG or MSGID.");
        return new() { Kind = ClStatementKind.SendProgramMessage, Command = call, Value = CommandParser.Unquote(value), Expression = Expression(value, line), MessageType = type, MessageId = id.Length > 0 ? id : null };
    }
    private void If(CommandCall call, Line line)
    {
        Check(call, "COND THEN", 2, line);
        var condition = Parameter(call, "COND", 0) ?? throw Error(line.Location, "IF requires COND.");
        var test = Emit(new() { Kind = ClStatementKind.If, Condition = condition, Expression = Expression(condition, line) }, line.Location);
        var body = Parameter(call, "THEN", 1); var legacy = body is null;
        if (legacy) Sequence(new[] { "ELSE", "ENDIF" }); else Command(new(body!, line.Location));
        if (_position < _lines.Count && Name(_lines[_position].Text) == "ELSE")
        {
            var skip = Emit(new() { Kind = ClStatementKind.Branch }, line.Location); _statements[test].Jump = _statements.Count;
            var alternative = _lines[_position++]; var otherwise = CommandParser.Parse(alternative.Text); Check(otherwise, "CMD", 1, alternative);
            var action = Parameter(otherwise, "CMD", 0);
            if (legacy && action is null) Sequence(new[] { "ENDIF" });
            else if (action is not null) Command(new(action, alternative.Location));
            else throw Error(alternative.Location, "Native ELSE requires CMD.");
            _statements[skip].Jump = _statements.Count;
        }
        else _statements[test].Jump = _statements.Count;
        if (legacy) End("ENDIF", line);
    }
    private void Group(CommandCall call, Line line, IReadOnlyList<string> labels)
    {
        if (_loops.Count >= 25) throw Error(line.Location, "DO groups exceed 25 nesting levels.");
        var kind = call.Name.ToUpperInvariant(); var loop = new Loop(kind, labels); _loops.Add(loop);
        var head = _statements.Count; int? guard = null;
        if (kind == "DO") Check(call, "", 0, line);
        else if (kind == "DOFOR")
        {
            Check(call, "VAR FROM TO BY", 3, line);
            var variable = Parameter(call, "VAR", 0)?.ToUpperInvariant() ?? throw Error(line.Location, "DOFOR requires VAR.");
            var from = Parameter(call, "FROM", 1) ?? throw Error(line.Location, "DOFOR requires FROM.");
            var to = Parameter(call, "TO", 2) ?? throw Error(line.Location, "DOFOR requires TO.");
            if (!long.TryParse(call.GetOption("BY") ?? "1", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var increment)) throw Error(line.Location, "DOFOR BY requires an integer constant.");
            guard = Emit(new() { Kind = ClStatementKind.ForBegin, VariableName = variable, Expression = Expression(from, line), TerminalExpression = Expression(to, line), Increment = increment }, line.Location);
        }
        else
        {
            Check(call, "COND", 1, line); var condition = Parameter(call, "COND", 0) ?? throw Error(line.Location, kind + " requires COND.");
            if (kind == "DOWHILE") guard = Emit(new() { Kind = ClStatementKind.If, Condition = condition, Expression = Expression(condition, line) }, line.Location);
        }
        Sequence(new[] { "ENDDO" }); var closing = End("ENDDO", line);
        var resume = _statements.Count;
        if (kind == "DOWHILE") Emit(new() { Kind = ClStatementKind.Branch, Jump = head }, closing.Location);
        else if (kind == "DOUNTIL")
        {
            var condition = Parameter(call, "COND", 0)!;
            Emit(new() { Kind = ClStatementKind.If, Condition = condition, Expression = Expression(condition, line), Jump = head }, closing.Location);
        }
        else if (kind == "DOFOR") Emit(new() { Kind = ClStatementKind.ForEnd, LoopHead = head }, closing.Location);
        if (guard is { } entry) _statements[entry].Jump = _statements.Count;
        foreach (var index in loop.Breaks) _statements[index].Jump = _statements.Count;
        foreach (var index in loop.Continues) _statements[index].Jump = resume;
        _loops.RemoveAt(_loops.Count - 1);
    }
    private void Select(CommandCall call, Line line)
    {
        Check(call, "", 0, line); var exits = new List<int>(); int? previous = null; var count = 0; var otherwise = false;
        while (_position < _lines.Count && Name(_lines[_position].Text) is "WHEN" or "OTHERWISE")
        {
            var branch = _lines[_position++]; var command = CommandParser.Parse(branch.Text);
            if (otherwise) throw Error(branch.Location, "OTHERWISE must be last in SELECT.");
            if (previous is { } test) _statements[test].Jump = _statements.Count;
            if (command.Name.Equals("WHEN", StringComparison.OrdinalIgnoreCase))
            {
                Check(command, "COND THEN", 2, branch); var condition = Parameter(command, "COND", 0) ?? throw Error(branch.Location, "WHEN requires COND.");
                previous = Emit(new() { Kind = ClStatementKind.If, Condition = condition, Expression = Expression(condition, branch) }, branch.Location); count++;
                if (Parameter(command, "THEN", 1) is { } body) Command(new(body, branch.Location));
            }
            else
            {
                Check(command, "CMD", 1, branch); otherwise = true; previous = null;
                if (Parameter(command, "CMD", 0) is { } body) Command(new(body, branch.Location));
            }
            exits.Add(Emit(new() { Kind = ClStatementKind.Branch }, branch.Location));
        }
        if (count == 0) throw Error(line.Location, "SELECT requires at least one WHEN.");
        if (_position < _lines.Count && Name(_lines[_position].Text) == "ENDSL") End("ENDSL", line); else End("ENDSELECT", line);
        if (previous is { } final) _statements[final].Jump = _statements.Count;
        foreach (var exit in exits) _statements[exit].Jump = _statements.Count;
    }
    private Line End(string command, Line opening)
    {
        if (_position >= _lines.Count || Name(_lines[_position].Text) != command) throw Error(opening.Location, "Missing matching " + command + ".");
        var line = _lines[_position++]; Check(CommandParser.Parse(line.Text), "", 0, line); return line;
    }
    private int Emit(ClStatement statement, SourceLocation location)
    {
        if (_statements.Count >= 100000) throw Error(location, "Expanded CL exceeds 100000 instructions.");
        statement.Location = location; _statements.Add(statement); return _statements.Count - 1;
    }
    private static ClExpression Expression(string text, Line line)
    {
        try { return ClExpression.Compile(text); } catch (ClRuntimeException error) { throw Error(line.Location, error.Message); }
    }
    private static string Name(string text) => text.Split(new[] { ' ', '\t', '(' }, 2)[0].ToUpperInvariant();
    private static string? Parameter(CommandCall call, string keyword, int position)
    {
        if (call.GetOption(keyword) is { } value) return value;
        if (position >= call.Positional.Count) return null;
        var positional = call.Positional[position]; return positional.StartsWith('(') && positional.EndsWith(')') ? positional[1..^1] : positional;
    }
    private static void Check(CommandCall call, string keywords, int positional, Line line)
    {
        var names = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Take(call.Positional.Count).Any(k => call.GetOption(k) is not null)) throw Error(line.Location, "Parameter supplied both positionally and by keyword.");
        if (call.Positional.Count > positional || call.Keywords.Keys.Any(k => !keywords.Split(' ').Contains(k.ToUpperInvariant())))
            throw Error(line.Location, "Unsupported " + call.Name + " parameter.");
    }
    private static ClCompileException Error(SourceLocation location, string message) => new(location, message);
}
