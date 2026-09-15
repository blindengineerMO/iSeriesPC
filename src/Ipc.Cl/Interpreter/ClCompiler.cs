using Ipc.Cl.Parsing;
using Ipc.Core.Compilation;

namespace Ipc.Cl.Interpreter;

public sealed class ClCompiler(Func<ClFileRequest, ClDatabaseFile>? fileResolver = null)
{
    public ClProgram Compile(string name, string library, string source, CancellationToken cancellationToken = default) => Compile(name, library, new SourceDocument(library + "/" + name, source), cancellationToken: cancellationToken);

    public ClProgram Compile(string name, string library, SourceDocument source,
        Func<SourceDocument, string, SourceDocument>? resolver = null, CancellationToken cancellationToken = default)
    {
        try { return Compile(name, library, new SourcePreprocessor(resolver).Expand(source, cancellationToken), cancellationToken); }
        catch (SourcePreprocessException error) { throw new ClCompileException(error); }
    }

    public ClProgram Compile(string name, string library, PreprocessedSource source, CancellationToken cancellationToken = default)
    {
        var lowerer = new ClControlFlowCompiler(source, cancellationToken, fileResolver);
        var statements = lowerer.Compile();
        var entryParameters = statements.LastOrDefault(s => s.Kind == ClStatementKind.Program)?.EntryParameters ?? Array.Empty<string>();
        var labels = ResolveLabels(statements);

        return new ClProgram
        {
            Name = name,
            Library = library,
            Source = source,
            Statements = statements,
            Monitors = lowerer.Monitors,
            Files = lowerer.Files,
            Labels = labels,
            EntryParameters = entryParameters,
        };
    }

    internal static ClStatement ParseStatement(string line)
    {
        var keyword = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();

        if (keyword == "DCL")
        {
            var call = CommandParser.Parse(line);
            return new ClStatement
            {
                Kind = ClStatementKind.Declare,
                VariableName = Parameter(call, "VAR", 0)?.ToUpperInvariant(),
                Value = Parameter(call, "VALUE", 3),
                DeclarationType = Parameter(call, "TYPE", 1)?.ToUpperInvariant(),
                DeclarationLength = Parameter(call, "LEN", 2),
            };
        }

        if (keyword == "CHGVAR")
        {
            var call = CommandParser.Parse(line);
            return new ClStatement
            {
                Kind = ClStatementKind.Change,
                VariableName = Parameter(call, "VAR", 0)?.ToUpperInvariant(),
                Value = CallOrEmpty(call, "VALUE") ?? Parameter(call, "VALUE", 1),
            };
        }

        if (keyword == "IF")
        {
            var call = CommandParser.Parse(line);
            var then = call.GetOption("THEN");
            if (then is null)
            {
                for (var i = 0; i + 1 < call.Positional.Count; i++)
                {
                    if (call.Positional[i].Equals("THEN", StringComparison.OrdinalIgnoreCase))
                    {
                        var group = call.Positional[i + 1];
                        if (group.StartsWith('(') && group.EndsWith(')'))
                        {
                            then = group[1..^1];
                        }

                        break;
                    }
                }
            }

            return new ClStatement
            {
                Kind = ClStatementKind.If,
                Condition = call.GetOption("COND"),
                Then = then,
            };
        }

        if (keyword == "ELSE")
        {
            return new ClStatement { Kind = ClStatementKind.Else };
        }

        if (keyword == "ENDIF")
        {
            return new ClStatement { Kind = ClStatementKind.EndIf };
        }

        if (keyword == "GOTO")
        {
            var call = CommandParser.Parse(line);
            var label = call.GetOption("CMDLBL") ?? call.Positional.FirstOrDefault();
            return new ClStatement
            {
                Kind = ClStatementKind.Goto,
                Label = CommandParser.Unquote(label),
            };
        }

        if (keyword == "CALL")
        {
            var call = CommandParser.Parse(line);
            var target = call.GetOption("PGM") ?? call.Positional.FirstOrDefault();
            return new ClStatement
            {
                Kind = ClStatementKind.Call,
                ProgramName = target,
                Parameters = call.Split("PARM"),
            };
        }

        if (keyword == "SNDPGMMSG")
        {
            var call = CommandParser.Parse(line);
            return new ClStatement
            {
                Kind = ClStatementKind.SendProgramMessage,
                Value = CommandParser.Unquote(call.GetOption("MSG"))!,
            };
        }

        if (keyword.EndsWith(':'))
        {
            return new ClStatement
            {
                Kind = ClStatementKind.Label,
                Label = keyword[..^1],
            };
        }

        var command = CommandParser.Parse(line);
        return new ClStatement
        {
            Kind = ClStatementKind.Command,
            Command = command,
        };
    }

    private static string? CallOrEmpty(CommandCall call, string keyword)
    {
        var value = call.GetOption(keyword);
        return value is null ? null : CommandParser.Unquote(value);
    }

    private static IReadOnlyDictionary<string, int> ResolveLabels(IReadOnlyList<ClStatement> statements)
    {
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i].Kind == ClStatementKind.Label)
            {
                if (!labels.TryAdd(statements[i].Label!, i)) throw new ClCompileException(statements[i].Location!, "Duplicate label " + statements[i].Label + ".");
            }
        }

        return labels;
    }

    private static string? Parameter(CommandCall call, string keyword, int position)
    {
        if (call.GetOption(keyword) is { } value) return value;
        if (position >= call.Positional.Count) return null;
        var valueAtPosition = call.Positional[position];
        return valueAtPosition.StartsWith('(') && valueAtPosition.EndsWith(')') ? valueAtPosition[1..^1] : valueAtPosition;
    }

}

public sealed class ClCompileException : Exception
{
    public SourceLocation? Location { get; }
    public ClCompileException(SourceLocation location, string message) : base($"IPC0006: {location}: {message}") { Location = location; }
    public ClCompileException(SourcePreprocessException error) : base(error.Message, error) { Location = error.Location; }

    public ClCompileException(string program, string library, int lineNumber, string message)
        : base($"{library}/{program}:{(lineNumber == 0 ? "?" : lineNumber.ToString())}: {message}")
    {
    }
}
