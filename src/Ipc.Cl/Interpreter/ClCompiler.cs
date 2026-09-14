using Ipc.Cl.Parsing;

namespace Ipc.Cl.Interpreter;

public sealed class ClCompiler
{
    public ClProgram Compile(string name, string library, string source)
    {
        var statements = new List<ClStatement>();
        IReadOnlyList<string> entryParameters = Array.Empty<string>();
        var lineNumber = 0;

        foreach (var raw in source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith("//"))
            {
                continue;
            }

            var upper = line.ToUpperInvariant();

            try
            {
                if (upper.StartsWith("PGM "))
                {
                    var call = CommandParser.Parse(line);
                    entryParameters = call.Split("PARM");
                    statements.Add(new ClStatement { Kind = ClStatementKind.Program, EntryParameters = entryParameters });
                    continue;
                }

                if (upper == "PGM" || upper.StartsWith("PGM("))
                {
                    var call = CommandParser.Parse(line);
                    entryParameters = call.Split("PARM");
                    statements.Add(new ClStatement { Kind = ClStatementKind.Program, EntryParameters = entryParameters });
                    continue;
                }

                if (upper == "ENDPGM")
                {
                    statements.Add(new ClStatement { Kind = ClStatementKind.EndProgram });
                    break;
                }

                statements.Add(ParseStatement(line));
            }
            catch (ClParseException ex)
            {
                throw new ClCompileException(name, library, lineNumber, ex.Message);
            }
        }

        var labels = ResolveLabels(statements);
        ResolveBlocks(statements);

        return new ClProgram
        {
            Name = name,
            Library = library,
            Statements = statements,
            Labels = labels,
            EntryParameters = entryParameters,
        };
    }

    private static ClStatement ParseStatement(string line)
    {
        var keyword = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();

        if (keyword == "DCL")
        {
            var call = CommandParser.Parse(line);
            return new ClStatement
            {
                Kind = ClStatementKind.Declare,
                VariableName = call.GetOption("VAR"),
                Value = CallOrEmpty(call, "VALUE"),
            };
        }

        if (keyword == "CHGVAR")
        {
            var call = CommandParser.Parse(line);
            return new ClStatement
            {
                Kind = ClStatementKind.Change,
                VariableName = call.GetOption("VAR"),
                Value = CallOrEmpty(call, "VALUE"),
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

        if (keyword == "SNDPGMMSG" || keyword == "SNDMSG")
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
                labels[statements[i].Label!] = i;
            }
        }

        return labels;
    }

    private static void ResolveBlocks(IReadOnlyList<ClStatement> statements)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i].Kind != ClStatementKind.If || statements[i].Then is not null)
            {
                continue;
            }

            var depth = 0;
            int? elseIndex = null;
            int? endIndex = null;

            for (var j = i + 1; j < statements.Count; j++)
            {
                switch (statements[j].Kind)
                {
                    case ClStatementKind.If when statements[j].Then is null:
                        depth++;
                        break;
                    case ClStatementKind.Else when depth == 0:
                        elseIndex = j;
                        break;
                    case ClStatementKind.EndIf when depth == 0:
                        endIndex = j;
                        goto found;
                    case ClStatementKind.EndIf:
                        depth--;
                        break;
                }
            }

        found:
            if (endIndex is null)
            {
                throw new ClCompileException(string.Empty, string.Empty, 0, "IF without matching ENDIF.");
            }

            statements[i].Jump = (elseIndex ?? endIndex)!.Value + 1;
            if (elseIndex is not null)
            {
                statements[elseIndex.Value].Jump = endIndex!.Value + 1;
            }
        }
    }
}

public sealed class ClCompileException : Exception
{
    public ClCompileException(string program, string library, int lineNumber, string message)
        : base($"{library}/{program}:{(lineNumber == 0 ? "?" : lineNumber.ToString())}: {message}")
    {
    }
}