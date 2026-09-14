using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static class RpgCompiler
{
    public static RpgProgram Compile(string library, string name, string source)
    {
        var program = new RpgProgram { Name = name.ToUpperInvariant(), Library = library.ToUpperInvariant() };
        var specificationLines = ReadSpecs(source);

        var freeForm = specificationLines.Any(l =>
            l.TrimStart().StartsWith("**free", StringComparison.OrdinalIgnoreCase));
        program.IsFreeForm = freeForm;
        ParseDeclarations(specificationLines, program);
        ParseStatements(specificationLines, program, freeForm);
        ExtractSubroutines(program);
        ExtractSubprocedures(program);
        ResolveBlocks(program.MainStatements);
        foreach (var subroutine in program.Subroutines)
        {
            ResolveBlocks(subroutine.Statements);
        }

        foreach (var subprocedure in program.Subprocedures)
        {
            ResolveBlocks(subprocedure.Statements);
        }

        AssignDsOffsets(program);
        return program;
    }

    private static List<string> ReadSpecs(string source)
    {
        var lines = new List<string>();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("*", StringComparison.Ordinal) &&
                !line.StartsWith("**free", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static void ParseDeclarations(List<string> lines, RpgProgram program)
    {
        RpgDataStructure? currentDs = null;
        RpgSubprocedure? currentProc = null;
        foreach (var line in lines)
        {
            var spec = line.Length >= 7 ? line[6] : ' ';
            if (spec != 'D' && spec != 'P' && !line.TrimStart().StartsWith("**free", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (spec == 'P')
            {
                var marker = line.Length >= 24 ? line[23] : ' ';
                var procName = Col(line, 8, 21).Trim();
                currentDs = null;
                if (marker == 'B' && procName.Length > 0)
                {
                    currentProc = new RpgSubprocedure { Name = procName.ToUpperInvariant() };
                    program.Subprocedures.Add(currentProc);
                }
                else if (marker == 'E')
                {
                    currentProc = null;
                }

                continue;
            }

            if (spec != 'D')
            {
                continue;
            }

            var name = Col(line, 8, 20).Trim();
            var type = Col(line, 22, 23).Trim().ToUpperInvariant();
            var lengthText = Col(line, 39, 40).Trim();
            var decimalsText = Col(line, 41, 42).Trim();
            var keywords = Col(line, 51, 80).Trim();

            if (type == "DS")
            {
                currentDs = new RpgDataStructure { Name = name };
                if (int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                {
                    currentDs.Length = length;
                }

                currentDs.Dimension = ParseKeywordInt(keywords, "DIM") ?? 1;
                program.DataStructures.Add(currentDs);
                continue;
            }

            if (currentDs is not null && type.Length == 0 && name.Length > 0)
            {
                var element = ParseDsElement(name, lengthText, decimalsText, keywords, currentDs);
                currentDs.Elements.Add(element);
                continue;
            }

            currentDs = null;
            var field = ParseStandalone(name, type, lengthText, decimalsText, keywords);
            if (field is not null)
            {
                program.Fields.Add(field);
                if (currentProc is not null)
                {
                    currentProc.Parameters.Add(field.Name);
                }
            }
        }
    }

    private static RpgDsElement ParseDsElement(string name, string lengthText, string decimalsText,
        string keywords, RpgDataStructure owner)
    {
        var length = ParseLength(lengthText, decimalsText, out var decimals);
        var element = new RpgDsElement
        {
            Name = name,
            Kind = !string.IsNullOrWhiteSpace(decimalsText) ? RpgFieldKind.Zoned : RpgFieldKind.Character,
            Length = length,
            Decimals = decimals,
        };

        var overlay = MatchKeyword(keywords, "OVERLAY");
        if (overlay is not null)
        {
            var target = overlay;
            var colon = overlay.IndexOf(':');
            if (colon >= 0)
            {
                target = overlay[..colon];
                if (int.TryParse(overlay[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                {
                    element.Offset = offset - 1;
                    element.HasExplicitOffset = true;
                    return element;
                }
            }

            var baseElement = owner.Find(target);
            if (baseElement is not null)
            {
                element.Offset = baseElement.Offset;
                element.HasExplicitOffset = true;
            }
        }

        return element;
    }

    private static RpgField? ParseStandalone(string name, string type, string lengthText, string decimalsText, string keywords)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (type == "C")
        {
            return new RpgField
            {
                Name = name,
                Kind = RpgFieldKind.Character,
                Length = ParseLength(lengthText, decimalsText, out var decimals),
                Decimals = decimals,
                Source = RpgFieldSource.Constant,
                InitialValue = ParseConstantValue(name, keywords),
            };
        }

        var length = ParseLength(lengthText, decimalsText, out var decimalPositions);
        var kind = !string.IsNullOrWhiteSpace(decimalsText) ? RpgFieldKind.Zoned : RpgFieldKind.Character;
        if (type is "P")
        {
            kind = RpgFieldKind.Packed;
        }
        else if (type is "B" or "I")
        {
            kind = RpgFieldKind.Binary;
        }
        else if (type is "F" or "G")
        {
            kind = RpgFieldKind.Float;
        }
        else if (type is "D")
        {
            kind = RpgFieldKind.Date;
            length = 10;
        }
        else if (type is "T")
        {
            kind = RpgFieldKind.Time;
            length = 8;
        }
        else if (type is "Z")
        {
            kind = RpgFieldKind.Timestamp;
            length = 26;
        }

        var field = new RpgField
        {
            Name = name,
            Kind = kind,
            Length = length,
            Decimals = decimalPositions,
            InitialValue = ParseInitialValue(keywords),
            Varying = HasKeyword(keywords, "VARYING"),
        };
        field.Dimension = ParseKeywordInt(keywords, "DIM") ?? 1;
        field.IsArray = field.Dimension > 1;
        field.Source = RpgFieldSource.Standalone;
        return field;
    }

    private static int ParseLength(string lengthText, string decimalsText, out int decimals)
    {
        decimals = int.TryParse(decimalsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0
            ? length
            : Math.Max(1, decimals + (decimals > 0 ? 1 : 1));
    }

    private static string? ParseInitialValue(string keywords)
    {
        var initial = MatchKeyword(keywords, "INZ");
        if (initial is null)
        {
            return null;
        }

        return Unquote(initial);
    }

    private static string? ParseConstantValue(string name, string keywords)
    {
        var constant = MatchKeyword(keywords, "CONST");
        if (constant is not null)
        {
            return Unquote(constant);
        }

        return decimal.TryParse(name, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? name : Unquote(name);
    }

    private static string? MatchKeyword(string keywords, string keyword)
    {
        var pattern = keyword + @"(?:\((.*?)\))?";
        var match = Regex.Match(keywords, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && match.Groups[1].Success ? match.Groups[1].Value : null;
    }

    private static bool HasKeyword(string keywords, string keyword) =>
        Regex.IsMatch(keywords, @"(?:^|[\s,])(?i)" + keyword + @"(?:\s|\)|$|,)", RegexOptions.CultureInvariant);

    private static int? ParseKeywordInt(string keywords, string keyword)
    {
        var value = MatchKeyword(keywords, keyword);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    private static void ParseStatements(List<string> lines, RpgProgram program, bool globalFree)
    {
        var inFreeBlock = false;
        foreach (var line in lines)
        {
            var spec = line.Length >= 7 ? line[6] : ' ';
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("**free", StringComparison.OrdinalIgnoreCase))
            {
                inFreeBlock = true;
                continue;
            }

            if (trimmed.StartsWith("/free", StringComparison.OrdinalIgnoreCase))
            {
                inFreeBlock = true;
                continue;
            }

            if (trimmed.StartsWith("/end-free", StringComparison.OrdinalIgnoreCase))
            {
                inFreeBlock = false;
                continue;
            }

            if (spec == 'P')
            {
                var marker = line.Length >= 24 ? line[23] : ' ';
                if (marker is 'B' or 'E')
                {
                    program.MainStatements.Add(new RpgStatement
                    {
                        Opcode = marker == 'B' ? RpgOpcode.BegProc : RpgOpcode.EndProc,
                        Factor1 = Col(line, 8, 21).Trim(),
                        LineNumber = 0,
                    });
                }

                continue;
            }

            if (globalFree || inFreeBlock)
            {
                if (spec != 'D')
                {
                    program.MainStatements.Add(ParseFreeStatement(line, program));
                }
            }
            else if (spec == 'C')
            {
                program.MainStatements.Add(ParseFixedStatement(line));
            }
        }
    }

    private static RpgStatement ParseFixedStatement(string line)
    {
        var conditions = ParseConditions(Col(line, 8, 17));
        var factor1 = Col(line, 18, 35).Trim();
        var opcode = Col(line, 36, 42).Trim().ToUpperInvariant();
        var factor2 = Col(line, 43, 47).Trim();
        var result = Col(line, 48, 58).Trim();
        var length = Col(line, 53, 58).Trim();
        var indicator1 = ParseIndicator(Col(line, 65, 66));
        var indicator2 = ParseIndicator(Col(line, 67, 68));
        var indicator3 = ParseIndicator(Col(line, 69, 70));

        var statement = new RpgStatement
        {
            Opcode = MapOpcode(opcode),
            Factor1 = factor1,
            Factor2 = factor2,
            Result = result,
            Length = length,
            LineNumber = 0,
            Indicator1 = indicator1,
            Indicator2 = indicator2,
            Indicator3 = indicator3,
        };
        statement.Conditions.AddRange(conditions);
        if (statement.Opcode is RpgOpcode.If or RpgOpcode.When && !string.IsNullOrWhiteSpace(statement.Factor2))
        {
            statement.Factor1 = (statement.Factor1 + " " + statement.Factor2).Trim();
            statement.Factor2 = null;
        }

        return statement;
    }

    private static RpgStatement ParseFreeStatement(string line, RpgProgram program)
    {
        var text = line.Trim().TrimStart('*').Trim();
        if (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        var space = text.IndexOfAny(new[] { ' ', '\t' });
        var opcode = (space >= 0 ? text[..space] : text).Trim().ToUpperInvariant();
        var operands = space >= 0 ? text[(space + 1)..].Trim() : string.Empty;

        var statement = new RpgStatement
        {
            Opcode = MapFreeOpcode(opcode),
            Factor1 = null,
            Factor2 = null,
            Result = null,
            Value = operands.Length > 0 ? operands : null,
            LineNumber = 0,
        };

        switch (statement.Opcode)
        {
            case RpgOpcode.Eval:
            case RpgOpcode.If:
            case RpgOpcode.When:
            case RpgOpcode.DoW:
            case RpgOpcode.DoU:
            case RpgOpcode.CallB:
            case RpgOpcode.CallP:
            case RpgOpcode.Else:
            case RpgOpcode.EndIf:
            case RpgOpcode.Select:
            case RpgOpcode.Other:
            case RpgOpcode.EndSl:
            case RpgOpcode.Do:
            case RpgOpcode.For:
            case RpgOpcode.EndDo:
            case RpgOpcode.Iter:
            case RpgOpcode.Leave:
            case RpgOpcode.Return:
            case RpgOpcode.Retrn:
            case RpgOpcode.EndSr:
            case RpgOpcode.Parm:
            case RpgOpcode.Plist:
            case RpgOpcode.OnError:
            case RpgOpcode.None:
                break;

            case RpgOpcode.BegSr:
            case RpgOpcode.Tag:
            case RpgOpcode.Exsr:
            case RpgOpcode.Goto:
            case RpgOpcode.Dsply:
            case RpgOpcode.SndPgMmsg:
            case RpgOpcode.SndMsg:
            case RpgOpcode.SetOn:
            case RpgOpcode.SetOff:
            case RpgOpcode.Test:
            case RpgOpcode.Open:
            case RpgOpcode.Close:
            case RpgOpcode.Write:
            case RpgOpcode.Update:
            case RpgOpcode.Delete:
                statement.Factor1 = operands;
                break;

            case RpgOpcode.Call:
            case RpgOpcode.Force:
                SplitCall(operands, statement);
                break;

            case RpgOpcode.Chain:
            case RpgOpcode.SetLl:
            case RpgOpcode.SetGt:
            case RpgOpcode.Read:
            case RpgOpcode.Reade:
            case RpgOpcode.ReadP:
            case RpgOpcode.ReadPe:
                ParseKeyFile(operands, statement);
                break;

            case RpgOpcode.Move:
            case RpgOpcode.MoveL:
            case RpgOpcode.MoveA:
                statement.Factor1 = statement.Value != null ? FirstOperand(statement.Value) : null;
                statement.Result = statement.Value != null ? RestOperands(statement.Value) : null;
                break;

            case RpgOpcode.Comp:
                statement.Factor1 = statement.Value != null ? FirstOperand(statement.Value) : null;
                statement.Factor2 = statement.Value != null ? RestOperands(statement.Value) : null;
                break;

            case RpgOpcode.Clear:
            case RpgOpcode.RcvMsg:
                statement.Result = operands;
                break;
        }

        return statement;
    }

    private static void SplitCall(string operands, RpgStatement statement)
    {
        operands = operands.Trim();
        if (operands.StartsWith('('))
        {
            var end = operands.IndexOf(')');
            if (end > 0)
            {
                statement.Factor2 = operands[1..end].Trim();
                statement.Factor1 = operands[(end + 1)..].Trim();
                return;
            }
        }

        SplitTwo(operands, statement);
    }

    private static string FirstOperand(string operands)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private static string RestOperands(string operands)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : string.Empty;
    }

    private static void ParseKeyFile(string operands, RpgStatement statement)
    {
        operands = operands.Trim();
        if (operands.StartsWith('('))
        {
            var end = operands.IndexOf(')');
            if (end > 0)
            {
                statement.Factor1 = operands[1..end];
                statement.Factor2 = operands[(end + 1)..].Trim();
                return;
            }
        }

        SplitTwo(operands, statement);
    }

    private static void SplitTwo(string operands, RpgStatement statement)
    {
        var parts = operands.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            statement.Factor1 = parts[0];
            statement.Factor2 = parts[1];
        }
        else if (parts.Length == 1)
        {
            statement.Factor1 = parts[0];
        }
    }

    private static List<RpgCondition> ParseConditions(string column)
    {
        var conditions = new List<RpgCondition>();
        var tokens = column.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var negated = token.StartsWith('N');
            var digits = negated ? token[1..] : token;
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator) &&
                indicator is >= 0 and <= 99)
            {
                conditions.Add(new RpgCondition { Indicator = indicator, Negated = negated });
            }
        }

        return conditions;
    }

    private static int ParseIndicator(string column)
    {
        var text = column.Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator) &&
               indicator is >= 0 and <= 99
            ? indicator
            : 0;
    }

    private static string Col(string line, int startColumn, int endColumn)
    {
        var start = Math.Min(startColumn - 1, line.Length);
        var end = Math.Min(endColumn, line.Length);
        return start >= end ? string.Empty : line[start..end];
    }

    private static RpgOpcode MapFreeOpcode(string opcode) => opcode switch
    {
        "EVAL" => RpgOpcode.Eval,
        "CALLP" => RpgOpcode.CallP,
        "CALLB" => RpgOpcode.CallB,
        "CALL" => RpgOpcode.Call,
        "IF" => RpgOpcode.If,
        "ELSE" => RpgOpcode.Else,
        "ENDIF" => RpgOpcode.EndIf,
        "SELECT" => RpgOpcode.Select,
        "WHEN" => RpgOpcode.When,
        "OTHER" => RpgOpcode.Other,
        "ENDSL" => RpgOpcode.EndSl,
        "DO" => RpgOpcode.Do,
        "DOU" => RpgOpcode.DoU,
        "DOW" => RpgOpcode.DoW,
        "FOR" => RpgOpcode.For,
        "ENDFOR" => RpgOpcode.EndDo,
        "ENDDO" => RpgOpcode.EndDo,
        "ITER" => RpgOpcode.Iter,
        "LEAVE" => RpgOpcode.Leave,
        "GOTO" => RpgOpcode.Goto,
        "TAG" => RpgOpcode.Tag,
        "BEGSR" => RpgOpcode.BegSr,
        "ENDSR" => RpgOpcode.EndSr,
        "EXSR" => RpgOpcode.Exsr,
        "ON-ERROR" => RpgOpcode.OnError,
        "RETURN" => RpgOpcode.Return,
        "RETRN" => RpgOpcode.Retrn,
        "MOVE" => RpgOpcode.Move,
        "MOVEL" => RpgOpcode.MoveL,
        "MOVA" => RpgOpcode.MoveA,
        "ZADD" => RpgOpcode.ZAdd,
        "ZSUB" => RpgOpcode.ZSub,
        "CLEAR" => RpgOpcode.Clear,
        "SETON" => RpgOpcode.SetOn,
        "SETOFF" => RpgOpcode.SetOff,
        "BITON" => RpgOpcode.BitOn,
        "BITOFF" => RpgOpcode.BitOff,
        "TEST" => RpgOpcode.Test,
        "CHAIN" => RpgOpcode.Chain,
        "READ" => RpgOpcode.Read,
        "READE" => RpgOpcode.Reade,
        "READP" => RpgOpcode.ReadP,
        "READPE" => RpgOpcode.ReadPe,
        "SETLL" => RpgOpcode.SetLl,
        "SETGT" => RpgOpcode.SetGt,
        "WRITE" => RpgOpcode.Write,
        "UPDATE" => RpgOpcode.Update,
        "DELETE" => RpgOpcode.Delete,
        "OPEN" => RpgOpcode.Open,
        "CLOSE" => RpgOpcode.Close,
        "FORCE" => RpgOpcode.Force,
        "DSPLY" => RpgOpcode.Dsply,
        "SNDMSG" => RpgOpcode.SndMsg,
        "SNDPGMMSG" => RpgOpcode.SndPgMmsg,
        "RCVMSG" => RpgOpcode.RcvMsg,
        _ => RpgOpcode.None,
    };

    private static RpgOpcode MapOpcode(string opcode) => opcode switch
    {
        "ADD" => RpgOpcode.Add,
        "SUB" => RpgOpcode.Subtract,
        "MULT" => RpgOpcode.Multiply,
        "DIV" => RpgOpcode.Divide,
        "MVR" => RpgOpcode.Mvr,
        "Z-ADD" => RpgOpcode.ZAdd,
        "Z-SUB" => RpgOpcode.ZSub,
        "EVAL" => RpgOpcode.Eval,
        "MOVE" => RpgOpcode.Move,
        "MOVEL" => RpgOpcode.MoveL,
        "MOVA" => RpgOpcode.MoveA,
        "CLEAR" => RpgOpcode.Clear,
        "BITON" => RpgOpcode.BitOn,
        "BITOFF" => RpgOpcode.BitOff,
        "SETON" => RpgOpcode.SetOn,
        "SETOFF" => RpgOpcode.SetOff,
        "TEST" => RpgOpcode.Test,
        "COMP" => RpgOpcode.Comp,
        "IF" => RpgOpcode.If,
        "ELSE" => RpgOpcode.Else,
        "ENDIF" => RpgOpcode.EndIf,
        "SELECT" => RpgOpcode.Select,
        "WHEN" => RpgOpcode.When,
        "OTHER" => RpgOpcode.Other,
        "ENDSL" => RpgOpcode.EndSl,
        "DO" => RpgOpcode.Do,
        "DOU" => RpgOpcode.DoU,
        "DOW" => RpgOpcode.DoW,
        "FOR" => RpgOpcode.For,
        "ENDDO" => RpgOpcode.EndDo,
        "ITER" => RpgOpcode.Iter,
        "LEAVE" => RpgOpcode.Leave,
        "GOTO" => RpgOpcode.Goto,
        "TAG" => RpgOpcode.Tag,
        "BEGSR" => RpgOpcode.BegSr,
        "ENDSR" => RpgOpcode.EndSr,
        "EXSR" => RpgOpcode.Exsr,
        "ON-ERROR" => RpgOpcode.OnError,
        "RETRN" => RpgOpcode.Retrn,
        "RETURN" => RpgOpcode.Return,
        "OPEN" => RpgOpcode.Open,
        "CLOSE" => RpgOpcode.Close,
        "CHAIN" => RpgOpcode.Chain,
        "READ" => RpgOpcode.Read,
        "READE" => RpgOpcode.Reade,
        "READP" => RpgOpcode.ReadP,
        "READPE" => RpgOpcode.ReadPe,
        "SETLL" => RpgOpcode.SetLl,
        "SETGT" => RpgOpcode.SetGt,
        "WRITE" => RpgOpcode.Write,
        "UPDATE" => RpgOpcode.Update,
        "DELETE" => RpgOpcode.Delete,
        "FORCE" => RpgOpcode.Force,
        "EXFMT" => RpgOpcode.ExFmt,
        "DSPLY" => RpgOpcode.Dsply,
        "CALL" => RpgOpcode.Call,
        "CALLB" => RpgOpcode.CallB,
        "CALLP" => RpgOpcode.CallP,
        "PARM" => RpgOpcode.Parm,
        "PLIST" => RpgOpcode.Plist,
        "SNDPGMMSG" => RpgOpcode.SndPgMmsg,
        "SNDMSG" => RpgOpcode.SndMsg,
        "RCVMSG" => RpgOpcode.RcvMsg,
        _ => RpgOpcode.None,
    };

    private static void ExtractSubroutines(RpgProgram program)
    {
        var main = program.MainStatements;
        List<RpgStatement>? current = null;
        RpgSubroutine? subroutine = null;
        var keep = new List<RpgStatement>();
        foreach (var statement in main)
        {
            switch (statement.Opcode)
            {
                case RpgOpcode.BegSr:
                    subroutine = new RpgSubroutine { Name = statement.Factor1 ?? string.Empty };
                    current = new List<RpgStatement>();
                    program.Subroutines.Add(subroutine);
                    break;
                case RpgOpcode.EndSr:
                    if (current is not null)
                    {
                        subroutine!.Statements.AddRange(current);
                        current = null;
                    }

                    break;
                default:
                    if (current is not null)
                    {
                        current.Add(statement);
                    }
                    else
                    {
                        keep.Add(statement);
                    }

                    break;
            }
        }

        if (current is not null)
        {
            subroutine!.Statements.AddRange(current);
        }

        program.MainStatements.Clear();
        program.MainStatements.AddRange(keep);
    }

    private static void ExtractSubprocedures(RpgProgram program)
    {
        var main = program.MainStatements;
        List<RpgStatement>? current = null;
        RpgSubprocedure? procedure = null;
        var keep = new List<RpgStatement>();
        foreach (var statement in main)
        {
            switch (statement.Opcode)
            {
                case RpgOpcode.BegProc:
                    procedure = program.FindSubprocedure(statement.Factor1 ?? string.Empty)
                        ?? new RpgSubprocedure { Name = (statement.Factor1 ?? string.Empty).ToUpperInvariant() };
                    if (!program.Subprocedures.Contains(procedure))
                    {
                        program.Subprocedures.Add(procedure);
                    }

                    current = new List<RpgStatement>();
                    break;
                case RpgOpcode.EndProc:
                    if (current is not null && procedure is not null)
                    {
                        procedure.Statements.AddRange(current);
                        current = null;
                        procedure = null;
                    }

                    break;
                default:
                    if (current is not null)
                    {
                        current.Add(statement);
                    }
                    else
                    {
                        keep.Add(statement);
                    }

                    break;
            }
        }

        if (current is not null && procedure is not null)
        {
            procedure.Statements.AddRange(current);
        }

        program.MainStatements.Clear();
        program.MainStatements.AddRange(keep);
    }

    private static void ResolveBlocks(List<RpgStatement> statements)
    {
        ResolveIfBlocks(statements);
        ResolveDoBlocks(statements);
        ResolveSelectBlocks(statements);
    }

    private static void ResolveIfBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<int>();
        var elseIndex = new Stack<int>();
        for (var index = 0; index < statements.Count; index++)
        {
            switch (statements[index].Opcode)
            {
                case RpgOpcode.If:
                    stack.Push(index);
                    elseIndex.Push(-1);
                    break;
                case RpgOpcode.Else:
                    if (stack.Count > 0)
                    {
                        elseIndex.Pop();
                        elseIndex.Push(index);
                    }

                    break;
                case RpgOpcode.EndIf:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(0, "ENDIF without a matching IF.");
                    }

                    var ifIndex = stack.Pop();
                    var elseIdx = elseIndex.Pop();
                    statements[ifIndex].Jump = elseIdx >= 0 ? elseIdx : index;
                    statements[ifIndex].End = index;
                    if (elseIdx >= 0)
                    {
                        statements[elseIdx].Jump = index;
                    }

                    break;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(0, "IF without a matching ENDIF.");
        }
    }

    private static void ResolveDoBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<int>();
        for (var index = 0; index < statements.Count; index++)
        {
            var opcode = statements[index].Opcode;
            if (opcode is RpgOpcode.Do or RpgOpcode.DoU or RpgOpcode.DoW or RpgOpcode.For)
            {
                stack.Push(index);
            }
            else if (opcode == RpgOpcode.EndDo)
            {
                if (stack.Count == 0)
                {
                    throw new RpgCompileException(0, "ENDDO without a matching DO/FOR.");
                }

                var start = stack.Pop();
                statements[start].End = index;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(0, "DO/FOR without a matching ENDDO.");
        }
    }

    private static void ResolveSelectBlocks(List<RpgStatement> statements)
    {
        var stack = new Stack<List<int>>();
        for (var index = 0; index < statements.Count; index++)
        {
            switch (statements[index].Opcode)
            {
                case RpgOpcode.Select:
                    stack.Push(new List<int>());
                    break;
                case RpgOpcode.When:
                case RpgOpcode.Other:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(0, "WHEN/OTHER outside of a SELECT.");
                    }

                    stack.Peek().Add(index);
                    break;
                case RpgOpcode.EndSl:
                    if (stack.Count == 0)
                    {
                        throw new RpgCompileException(0, "ENDSL without a matching SELECT.");
                    }

                    var members = stack.Pop();
                    var end = index;
                    statements[end].Jump = -1;
                    for (var member = 0; member < members.Count; member++)
                    {
                        var bound = member + 1 < members.Count ? members[member + 1] : end;
                        statements[members[member]].Jump = bound;
                        statements[members[member]].End = end;
                    }

                    break;
            }
        }

        if (stack.Count > 0)
        {
            throw new RpgCompileException(0, "SELECT without a matching ENDSL.");
        }
    }

    private static void AssignDsOffsets(RpgProgram program)
    {
        foreach (var ds in program.DataStructures)
        {
            var extent = 0;
            var offset = 0;
            foreach (var element in ds.Elements)
            {
                if (!element.HasExplicitOffset)
                {
                    element.Offset = offset;
                    offset += element.Length;
                }

                extent = Math.Max(extent, element.Offset + element.Length);
            }

            ds.Length = ds.Length <= 0 ? extent : Math.Max(ds.Length, extent);
        }
    }
}