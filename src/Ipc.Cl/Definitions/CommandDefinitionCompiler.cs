using System.Globalization;
using System.Text;
using Ipc.Cl.Parsing;
using Ipc.Core.Objects;

namespace Ipc.Cl.Definitions;

public sealed class CommandDefinitionCompiler
{
    private static readonly HashSet<string> ParameterKeywords = new("KWD TYPE LEN MIN MAX DFT RSTD VALUES SPCVAL RANGE PROMPT CASE FILE INLPMTLEN".Split(' '), StringComparer.OrdinalIgnoreCase);
    public CommandDefinition Compile(string source, string processingProgram, string sourceName = "*SOURCE", string? helpGroup = null, string? helpId = null)
    {
        if (Encoding.UTF8.GetByteCount(source) > 1048576) throw new ArgumentException("Command source exceeds 1 MiB.");
        var statements = Statements(source, sourceName).ToArray();
        var commands = statements.Where(s => s.Call.Name == "CMD").ToArray();
        if (commands.Length != 1) throw Error(sourceName, 1, "Exactly one CMD statement is required.");
        Check(commands[0], new HashSet<string>(new[] { "PROMPT" }), sourceName);
        (string Text, int Order) PromptAt(Statement statement, string fallback)
        {
            try { return Prompt(statement.Call.GetOption("PROMPT"), fallback); }
            catch (ArgumentException error) { throw Error(sourceName, statement.Line, error.Message); }
        }
        var title = PromptAt(commands[0], "").Text;
        var qualifiers = new Dictionary<string, List<Statement>>(StringComparer.OrdinalIgnoreCase); string? label = null;
        foreach (var statement in statements.Where(s => s.Call.Name == "QUAL"))
        {
            label = statement.Label ?? label;
            if (label is null) throw Error(sourceName, statement.Line, "QUAL requires a statement label.");
            if (!qualifiers.TryGetValue(label, out var list)) qualifiers[label] = list = new(); list.Add(statement);
        }
        var parameters = new List<ParameterDefinition>();
        foreach (var statement in statements)
        {
            if (statement.Call.Name is "CMD" or "QUAL") continue;
            if (statement.Call.Name != "PARM") throw Error(sourceName, statement.Line, "Unsupported command-definition statement " + statement.Call.Name + ".");
            Check(statement, ParameterKeywords, sourceName);
            var call = statement.Call;
            string Value(string key, string fallback = "") => CommandParser.Unquote(call.GetOption(key) ?? fallback);
            int Number(string key, int fallback) => call.GetOption(key) is null ? fallback : int.TryParse(Value(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw Error(sourceName, statement.Line, "Invalid " + key + ".");
            bool Yes(string key) => Value(key, "*NO").ToUpperInvariant() switch { "*YES" => true, "*NO" => false, _ => throw Error(sourceName, statement.Line, key + " must be *YES or *NO.") };
            var type = Value("TYPE").ToUpperInvariant(); string? defaultLibrary = null;
            if (qualifiers.TryGetValue(type, out var qualification))
            {
                if (qualification.Count != 2) throw Error(sourceName, statement.Line, "Object qualification requires two QUAL levels (object and library).");
                foreach (var level in qualification)
                {
                    Check(level, new HashSet<string>(new[] { "TYPE", "LEN", "DFT", "SPCVAL", "PROMPT" }), sourceName);
                    if (level.Call.GetOption("TYPE")?.ToUpperInvariant() != "*NAME" || level.Call.GetOption("LEN") is { } size && size != "10") throw Error(sourceName, level.Line, "Object QUAL levels require TYPE(*NAME) LEN(10).");
                    if (level.Call.GetOption("SPCVAL") is { } values && CommandParser.Tokenize(values).Any(v => v.Trim('(', ')') is not ("*LIBL" or "*CURLIB"))) throw Error(sourceName, level.Line, "QUAL supports *LIBL and *CURLIB special libraries.");
                }
                if (qualification[0].Call.GetOption("DFT") is not null || qualification[0].Call.GetOption("SPCVAL") is not null) throw Error(sourceName, qualification[0].Line, "Object QUAL level cannot have a default or special value.");
                defaultLibrary = CommandParser.Unquote(qualification[1].Call.GetOption("DFT") ?? "*LIBL").ToUpperInvariant();
                if (defaultLibrary is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(defaultLibrary)) throw Error(sourceName, statement.Line, "Invalid default library.");
                type = "*NAME";
            }
            var sizes = CommandParser.Tokenize(call.GetOption("LEN") ?? (type is "*NAME" or "*GENERIC" ? "10" : type == "*LGL" ? "1" : type == "*DEC" ? "15 5" : "32"));
            if (sizes.Count is < 1 or > 2 || !int.TryParse(sizes[0], out var length) || sizes.Count == 2 && !int.TryParse(sizes[1], out _)) throw Error(sourceName, statement.Line, "Invalid LEN.");
            var decimals = sizes.Count == 2 ? int.Parse(sizes[1], CultureInfo.InvariantCulture) : 0;
            if (decimals != 0 && type != "*DEC") throw Error(sourceName, statement.Line, "Decimal positions require TYPE(*DEC).");
            var prompt = PromptAt(statement, Value("KWD"));
            var range = CommandParser.Tokenize(call.GetOption("RANGE") ?? "");
            decimal? low = null, high = null;
            if (range.Count > 0)
            {
                if (range.Count != 2 || !decimal.TryParse(range[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var l) || !decimal.TryParse(range[1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var h)) throw Error(sourceName, statement.Line, "RANGE requires two numeric constants.");
                low = l; high = h;
            }
            var special = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in CommandParser.Tokenize(call.GetOption("SPCVAL") ?? ""))
            {
                var pair = CommandParser.Tokenize(entry.StartsWith('(') && entry.EndsWith(')') ? entry[1..^1] : entry);
                if (pair.Count is < 1 or > 2 || !special.TryAdd(CommandParser.Unquote(pair[0]).ToUpperInvariant(), pair.Count == 1 ? pair[0] : pair[1])) throw Error(sourceName, statement.Line, "Invalid or duplicate SPCVAL.");
            }
            var casing = Value("CASE", "*MONO").ToUpperInvariant(); if (casing is not ("*MONO" or "*MIXED")) throw Error(sourceName, statement.Line, "CASE must be *MONO or *MIXED.");
            var promptLength = Value("INLPMTLEN", "*CALC").ToUpperInvariant();
            if (promptLength is not ("*CALC" or "*PWD")) throw Error(sourceName, statement.Line, "INLPMTLEN supports *CALC or *PWD.");
            parameters.Add(new() { Keyword = Value("KWD").ToUpperInvariant(), Type = type, Length = length, Decimals = decimals,
                Minimum = Number("MIN", 0), Maximum = Number("MAX", 1), Default = call.GetOption("DFT"), Prompt = prompt.Text,
                PromptOrder = prompt.Order, MixedCase = casing == "*MIXED", Secret = promptLength == "*PWD", Restricted = Yes("RSTD"), FileUsage = Value("FILE", "*NO").ToUpperInvariant(),
                DefaultLibrary = defaultLibrary, Values = CommandParser.Tokenize(call.GetOption("VALUES") ?? "").Select(v => v.StartsWith('\'') ? CommandParser.Unquote(v) : v.ToUpperInvariant()).ToArray(), SpecialValues = special,
                RangeMinimum = low, RangeMaximum = high, SourceLine = statement.Line });
            try { new CommandDefinition { ProcessingProgram = processingProgram, Parameters = new[] { parameters[^1] } }.Validate(); }
            catch (Exception error) when (error is ArgumentException or Ipc.Core.Messages.CpfException) { throw Error(sourceName, statement.Line, error.Message); }
            if (parameters.Count(p => p.Keyword == parameters[^1].Keyword) != 1) throw Error(sourceName, statement.Line, "Repeated parameter keyword.");
        }
        var definition = new CommandDefinition { Title = title, ProcessingProgram = QualifiedName.Parse(processingProgram).ToString(), Parameters = parameters, MaximumPositional = parameters.Count, HelpPanelGroup = helpGroup, HelpId = helpId };
        try { definition.Validate(); } catch (Exception error) when (error is ArgumentException or Ipc.Core.Messages.CpfException) { throw Error(sourceName, parameters.LastOrDefault()?.SourceLine ?? 1, error.Message); }
        return definition;
    }
    private static (string Text, int Order) Prompt(string? raw, string fallback)
    {
        if (raw is null) return (fallback, 0);
        var parts = CommandParser.Tokenize(raw);
        if (parts.Count is < 1 or > 2 || !parts[0].StartsWith('\'') || parts.Count == 2 && !int.TryParse(parts[1], out _)) throw new ArgumentException("PROMPT requires quoted text and an optional numeric order.");
        return (CommandParser.Unquote(parts[0]), parts.Count == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
    }
    private static void Check(Statement statement, HashSet<string> allowed, string sourceName)
    {
        if (statement.Call.Positional.Count != 0) throw Error(sourceName, statement.Line, "Use keyword parameters in command definitions.");
        foreach (var key in statement.Call.Keywords.Keys) if (!allowed.Contains(key)) throw Error(sourceName, statement.Line, "Unsupported definition parameter " + key + ".");
    }
    private sealed record Statement(CommandCall Call, int Line, string? Label);
    private static IEnumerable<Statement> Statements(string source, string sourceName)
    {
        var buffer = new StringBuilder(); var lineNumber = 0; var start = 0; var comment = false; var quoted = false;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++; if (lineNumber > 10000) throw Error(sourceName, lineNumber, "Command source exceeds 10000 lines.");
            var text = new StringBuilder();
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (comment) { if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { comment = false; i++; text.Append(' '); } continue; }
                if (!quoted && c == '/' && i + 1 < line.Length && line[i + 1] == '*') { comment = true; i++; text.Append(' '); continue; }
                text.Append(c);
                if (c == '\'') { if (quoted && i + 1 < line.Length && line[i + 1] == '\'') text.Append(line[++i]); else quoted = !quoted; }
            }
            var part = text.ToString().Trim(); if (part.Length == 0) continue;
            if (start == 0) start = lineNumber;
            var continued = !quoted && part[^1] is '+' or '-';
            if (continued) { buffer.Append(part[..^1]); if (part[^1] == '+') buffer.Append(' '); continue; }
            buffer.Append(part); var complete = buffer.ToString(); buffer.Clear(); string? label = null;
            var colon = complete.IndexOf(':'); var firstSpace = complete.IndexOf(' ');
            if (colon > 0 && (firstSpace < 0 || colon < firstSpace)) { label = complete[..colon].ToUpperInvariant(); if (!ObjectName.IsValid(label)) throw Error(sourceName, start, "Invalid statement label."); complete = complete[(colon + 1)..].TrimStart(); }
            CommandCall call;
            try { var parsed = CommandParser.Parse(complete); call = new() { Name = parsed.Name.ToUpperInvariant(), Keywords = parsed.Keywords, Positional = parsed.Positional }; }
            catch (ClParseException error) { throw Error(sourceName, start, error.Message); }
            yield return new(call, start, label); start = 0;
        }
        if (comment || quoted || buffer.Length > 0) throw Error(sourceName, start == 0 ? lineNumber : start, "Unterminated comment, quote, or continuation.");
    }
    private static ArgumentException Error(string source, int line, string message) => new($"IPC0006: {source}:{line}:1: {message}");
}
