using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static bool IsNativeCalculation(string text) => text.Length >= 6 && text[..5].All(c => c == ' ' || char.IsAsciiDigit(c)) && char.ToUpperInvariant(text[5]) == 'C';
    private static string NativeCalculationOperation(string text) => Regex.Replace(Col(text, 26, 35).ToUpperInvariant(), @"\s", "");
    private static string NativeOperationName(string text) => text.Split('(')[0];
    private static bool NativeExtendedOperation(string op) => MapNativeExtendedOpcode(op) != RpgOpcode.None;

    private static void DeclareNativeCalculationResult(RpgProgram program, string text, int line, bool procedure)
    {
        if (Col(text, 7, 35).All(c => c == ' ')) return;
        if (NativeExtendedOperation(NativeOperationName(NativeCalculationOperation(text)))) return;
        var length = Col(text.PadRight(80), 64, 68); var decimals = Col(text.PadRight(80), 69, 70);
        if (length.All(c => c == ' ') && decimals.All(c => c == ' ')) return;
        if (procedure || !Regex.IsMatch(length, @"\A *[0-9]+\z") || !int.TryParse(length, out var size) || size < 1 ||
            decimals.Any(c => c != ' ') && !Regex.IsMatch(decimals, @"\A *[0-9]+\z"))
            throw new RpgCompileException(line, "Native result definitions require global scope and right-justified length/decimal digits.");
        var name = Col(text, 50, 63).Trim().ToUpperInvariant();
        if (!RpgWorkstationBindings.Name(name)) throw new RpgCompileException(line, "An inline result definition requires a scalar field name.");
        var scale = string.IsNullOrWhiteSpace(decimals) ? (int?)null : int.Parse(decimals, CultureInfo.InvariantCulture);
        if (program.FindField(name) is { } field)
        {
            if (field.IsArray || field.Source == RpgFieldSource.Constant || field.Length != size || scale is { } value &&
                (field.Decimals != value || field.Kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer)))
                throw new RpgCompileException(line, "Inline result layout differs from its declared scalar field.");
            return;
        }
        DeclareFreeScalar(program, name, scale is { } places ? $"PACKED({size}:{places})" : $"CHAR({size})", line);
    }

    private static RpgStatement ParseNativeCalculation(List<string> lines, ref int index, RpgProgram program, CancellationToken cancellationToken)
    {
        var line = index + 1; var raw = lines[index].PadRight(80); var operation = NativeCalculationOperation(raw); var op = NativeOperationName(operation);
        var control = Col(raw, 7, 8).Trim().ToUpperInvariant();
        if (control is not ("" or "SR")) throw new RpgCompileException(line, "Native cycle/control-level and AN/OR indicator groups remain unsupported.");
        var condition = Col(raw, 9, 11).ToUpperInvariant();
        if (condition[0] is not (' ' or 'N') || condition[1..] != "  " && !NativeGeneralIndicator(condition[1..]) || condition[0] == 'N' && condition[1..] == "  ")
            throw new RpgCompileException(line, "Native conditioning requires an optional N and a general indicator 01-99.");
        if (operation.Length == 0 || raw[25] == ' ') throw new RpgCompileException(line, "Native calculation operation must begin in column 26.");
        if (!Regex.IsMatch(Col(raw, 26, 35).TrimEnd(), @"\A[A-Z][A-Z-]*(?:\s*\(\s*[A-Z](?:\s*[A-Z])*\s*\))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new RpgCompileException(line, "Native operation names cannot contain embedded blanks.");
        string? Operand(int first, int last)
        {
            var value = Col(raw, first, last); if (string.IsNullOrWhiteSpace(value)) return null;
            if (value[0] == ' ') throw new RpgCompileException(line, "Native operands must begin in their assigned columns.");
            _ = FixedDefinitionKeywords(value, line); return value.TrimEnd();
        }
        var factor1 = Operand(12, 25); RpgStatement statement;
        if (NativeExtendedOperation(op))
        {
            if (factor1 is not null) throw new RpgCompileException(line, "Extended factor 2 requires a blank factor 1.");
            var text = new StringBuilder(FixedDefinitionKeywords(Col(raw, 36, 80), line)); var next = index + 1;
            while (next < lines.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(lines[next])) { next++; continue; }
                if (!IsNativeCalculation(lines[next]) || Col(lines[next], 7, 35).Any(c => c != ' ')) break;
                text.Append(' ').Append(FixedDefinitionKeywords(Col(lines[next], 36, 80), next + 1)); index = next++;
            }
            var error = operation == "CALLP(E)";
            if (op is "EVAL" or "CALLP" && string.IsNullOrWhiteSpace(text.ToString()))
                throw new RpgCompileException(line, "Native EVAL/CALLP requires an expression or call target.");
            if (operation.Contains('(') && !(op == "SORTA" && operation is "SORTA(A)" or "SORTA(D)") && !error)
                throw new RpgCompileException(line, "Unsupported native extended-operation extender.");
            statement = ParseFreeStatement((error ? "CALLP" : operation) + " " + text, program, line);
            statement.ErrorExtender = error;
        }
        else
        {
            if (Col(raw, 77, 80).Any(c => c != ' ')) throw new RpgCompileException(line, "Native standard calculations require blank columns 77-80.");
            var factor2 = Operand(36, 49); var result = Operand(50, 63);
            var errorCall = operation == "CALL(E)";
            var (noLock, error) = ParseFileExtenders(ref operation, line);
            if (errorCall) { operation = "CALL"; error = true; }
            var mapped = MapNativeOpcode(operation);
            if (mapped == RpgOpcode.None) throw new RpgCompileException(line, "Unsupported native fixed operation " + operation + ".");
            var slots = new[] { Col(raw, 71, 72).ToUpperInvariant(), Col(raw, 73, 74).ToUpperInvariant(), Col(raw, 75, 76).ToUpperInvariant() };
            if (slots.Any(s => s != "  " && !NativeGeneralIndicator(s) && !(op is "SETON" or "SETOFF" && s == "LR")))
                throw new RpgCompileException(line, "Native result indicators require 01-99; SETON/SETOFF also supports LR.");
            int Flag(int at) => NativeGeneralIndicator(slots[at]) ? int.Parse(slots[at], CultureInfo.InvariantCulture) : 0;
            statement = new() { Opcode = mapped, Factor1 = factor1, Factor2 = factor2, Result = result, NoLock = noLock, ErrorExtender = error,
                LineNumber = line, Indicator1 = Flag(0), Indicator2 = Flag(1), Indicator3 = Flag(2) };
            void Require(bool valid, string message) { if (!valid) throw new RpgCompileException(line, message); }
            void NoIndicators() => Require(slots.All(s => s == "  "), "Result indicators are unsupported for this native operation.");
            switch (mapped)
            {
                case RpgOpcode.Add: case RpgOpcode.Subtract: case RpgOpcode.Multiply: case RpgOpcode.Divide:
                case RpgOpcode.ZAdd: case RpgOpcode.ZSub: case RpgOpcode.XFoot:
                    Require(factor2 is not null && result is not null, "Native arithmetic requires factor 2 and a result.");
                    Require(mapped is not (RpgOpcode.ZAdd or RpgOpcode.ZSub or RpgOpcode.XFoot) || factor1 is null, "This native arithmetic operation requires blank factor 1.");
                    NativeNumericOperand(program, result!, line, variable: true, array: slots.All(s => s == "  "));
                    NativeNumericOperand(program, factor2!, line, requireArray: mapped == RpgOpcode.XFoot);
                    if (factor1 is not null) NativeNumericOperand(program, factor1, line);
                    break;
                case RpgOpcode.Comp:
                    Require(factor1 is not null && factor2 is not null && result is null && slots.Any(s => s != "  "), "COMP requires two factors, a resulting indicator and no result."); break;
                case RpgOpcode.Lookup:
                    Require(factor1 is not null && factor2 is not null && slots.Any(s => s != "  ") && (slots[0] == "  " || slots[1] == "  "),
                        "LOOKUP requires a search argument, array/table and EQ/HI/LO indicators; HI and LO cannot combine.");
                    ValidateFixedLookup(program, statement, line); break;
                case RpgOpcode.SetOn: case RpgOpcode.SetOff:
                    Require(factor1 is null && factor2 is null && result is null && slots.Any(s => s != "  "), "SETON/SETOFF requires only resulting indicators.");
                    statement.Factor1 = string.Join(' ', slots.Where(s => s != "  ")); statement.Indicator1 = statement.Indicator2 = statement.Indicator3 = 0; break;
                case RpgOpcode.Plist:
                    Require(factor1 is not null && factor2 is null && result is null && condition == "   ", "PLIST requires only factor 1 and no conditioning."); NoIndicators(); break;
                case RpgOpcode.Parm:
                    Require(factor1 is null && factor2 is null && result is not null && condition == "   ", "PARM requires only a result; copy operands remain unsupported."); NoIndicators();
                    Require(program.FindField(result!) is { Source: not RpgFieldSource.Constant }, "PARM requires a declared field.");
                    statement.Factor1 = result; statement.Result = null; break;
                case RpgOpcode.Call:
                    Require(factor1 is null && factor2 is not null && slots[0] == "  " && slots[2] == "  ", "CALL requires factor 2, an optional PLIST result and optional LO error indicator; LR result remains unsupported.");
                    Require(!error || slots[1] == "  ", "CALL(E) cannot combine an error indicator.");
                    statement.Factor1 = factor2; statement.Factor2 = result; statement.Result = null; statement.Indicator1 = Flag(1); statement.Indicator2 = 0; break;
                case RpgOpcode.Do:
                    NoIndicators();
                    if (factor1 is not null) NativeNumericOperand(program, factor1, line, integer: true, array: false);
                    if (factor2 is not null) NativeNumericOperand(program, factor2, line, integer: true, array: false);
                    if (result is null)
                    {
                        var counter = new RpgField { Name = "\u0001DO" + line, Kind = RpgFieldKind.Integer, Length = 20, NativeScalar = true, DeclarationLine = line };
                        program.Fields.Add(counter); DeclareNativeStandalone(program, counter, line); statement.Result = counter.Name;
                    }
                    else NativeNumericOperand(program, result, line, variable: true, integer: true, array: false);
                    break;
                case RpgOpcode.EndDo:
                    Require(factor1 is null && result is null && condition == "   ", "Native loop endings cannot have factor 1, result or conditioning."); NoIndicators();
                    if (factor2 is not null) NativeNumericOperand(program, factor2, line, integer: true, array: false); break;
                case RpgOpcode.Tag: case RpgOpcode.BegSr:
                    Require(factor1 is not null && factor2 is null && result is null && condition == "   ", "TAG/BEGSR requires only factor 1 and no conditioning."); NoIndicators(); break;
                case RpgOpcode.Exsr: case RpgOpcode.Goto:
                    Require(factor1 is null && factor2 is not null && result is null, "EXSR/GOTO requires only factor 2."); NoIndicators(); statement.Factor1 = factor2; statement.Factor2 = null; break;
                case RpgOpcode.EndSr:
                    Require(factor1 is null && result is null && condition == "   ", "ENDSR permits only its optional return point in factor 2."); NoIndicators(); break;
                case RpgOpcode.Clear:
                    Require(factor1 is null && factor2?.ToUpperInvariant() is null or "*ALL" && result is not null, "CLEAR requires a result and optional *ALL in factor 2."); NoIndicators(); statement.ClearAllOccurrences = factor2?.ToUpperInvariant() == "*ALL"; break;
                case RpgOpcode.Dsply:
                    Require(factor1 is not null && factor2 is null && result is null, "Native DSPLY currently supports a display expression without queue/reply operands."); NoIndicators(); break;
                case RpgOpcode.Open: case RpgOpcode.Close: case RpgOpcode.Unlock:
                case RpgOpcode.Read: case RpgOpcode.ReadP: case RpgOpcode.ReadC: case RpgOpcode.Write: case RpgOpcode.Update: case RpgOpcode.Delete: case RpgOpcode.ExFmt:
                    Require(factor1 is null && factor2 is not null && (result is null || RecordIo(mapped)), "Native file operation requires factor 2 and a supported result data structure.");
                    ValidateNativeFileIndicators(mapped, slots, error, line); break;
                case RpgOpcode.Chain: case RpgOpcode.SetLl: case RpgOpcode.SetGt: case RpgOpcode.Reade: case RpgOpcode.ReadPe:
                    Require(factor2 is not null && (result is null || RecordIo(mapped)), "Native keyed input requires factor 2 and a supported result data structure."); ValidateNativeFileIndicators(mapped, slots, error, line); break;
                default:
                    Require(factor1 is null && factor2 is null && result is null && condition == "   ", "Native control operation has unsupported operands or conditioning."); NoIndicators(); break;
            }
        }
        statement.NativeOperation = op; statement.NativeControlLevel = control;
        if (condition[1..] != "  ") statement.Conditions.Add(new() { Indicator = int.Parse(condition[1..], CultureInfo.InvariantCulture), Negated = condition[0] == 'N' });
        return statement;
    }

    private static bool NativeGeneralIndicator(string value) => value.Length == 2 && value.All(char.IsAsciiDigit) && value != "00";
    private static void ValidateNativeParameterLists(RpgProgram program)
    {
        if (program.HasNativeEntryInterface && program.MainStatements.FirstOrDefault(s => s.Opcode == RpgOpcode.Plist && s.Factor1?.Equals("*ENTRY", StringComparison.OrdinalIgnoreCase) == true) is { } conflict)
            throw new RpgCompileException(conflict.LineNumber, "A main PI cannot be combined with an *ENTRY PLIST.");
        var scope = ""; var declarations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); var lists = 0;
        for (var position = 0; position < program.MainStatements.Count; position++)
        {
            var statement = program.MainStatements[position];
            if (statement.Opcode == RpgOpcode.BegProc) scope = statement.Factor1 ?? "";
            if (statement.Opcode == RpgOpcode.EndProc) scope = "";
            if (statement.NativeOperation != "PLIST") continue;
            if (++lists > 256) throw new RpgCompileException(statement.LineNumber, "Native parameter declarations exceed 256 lists.");
            var name = statement.Factor1!;
            if (!name.Equals("*ENTRY", StringComparison.OrdinalIgnoreCase) && !RpgWorkstationBindings.Name(name)) throw new RpgCompileException(statement.LineNumber, "Invalid native PLIST name.");
            if (!declarations.TryGetValue(scope, out var names)) declarations.Add(scope, names = new(StringComparer.OrdinalIgnoreCase));
            if (!names.Add(name)) throw new RpgCompileException(statement.LineNumber, "Duplicate native PLIST in this procedure.");
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ordered = new List<string>(); var count = 0;
            for (var index = position + 1; index < program.MainStatements.Count && program.MainStatements[index].Opcode == RpgOpcode.Parm; index++)
            {
                var parameter = program.MainStatements[index]; var field = program.FindField(parameter.Factor1!);
                if (++count > 255 || name.Equals("*ENTRY", StringComparison.OrdinalIgnoreCase) &&
                    (field is null || field.IsArray || field.IsTable || field.Source == RpgFieldSource.Constant || !fields.Add(field.Name)))
                    throw new RpgCompileException(parameter.LineNumber, "Native entry lists require distinct scalar fields; parameter lists are bounded to 255 arguments.");
                if (!name.Equals("*ENTRY", StringComparison.OrdinalIgnoreCase)) fields.Add(parameter.Factor1!);
                ordered.Add(parameter.Factor1!);
            }
            if (fields.Count == 0) throw new RpgCompileException(statement.LineNumber, "Native PLIST requires immediately following PARM fields.");
            program.NativeParameterLists.Add(scope + "/" + name, ordered.AsReadOnly());
        }
        scope = "";
        for (var position = 0; position < program.MainStatements.Count; position++)
        {
            var statement = program.MainStatements[position];
            if (statement.Opcode == RpgOpcode.BegProc) scope = statement.Factor1 ?? "";
            if (statement.Opcode == RpgOpcode.EndProc) scope = "";
            if (statement.NativeOperation == "CALL" && statement.Factor2 is { } list &&
                (!declarations.TryGetValue(scope, out var names) || !names.Contains(list)))
                throw new RpgCompileException(statement.LineNumber, "Native CALL names an undeclared parameter list.");
            if (statement.NativeOperation == "PARM" && (position == 0 || program.MainStatements[position - 1].Opcode is not (RpgOpcode.Plist or RpgOpcode.Parm or RpgOpcode.Call)))
                throw new RpgCompileException(statement.LineNumber, "Native PARM requires a preceding PLIST/CALL/PARM.");
            if (statement.NativeOperation == "CALL" && statement.Factor2 is not null && position + 1 < program.MainStatements.Count && program.MainStatements[position + 1].Opcode == RpgOpcode.Parm)
                throw new RpgCompileException(statement.LineNumber, "Native CALL cannot combine a named PLIST with inline PARM fields.");
            if (statement.NativeOperation == "CALL" && statement.Factor2 is null)
                for (var index = position + 1; index < program.MainStatements.Count && program.MainStatements[index].Opcode == RpgOpcode.Parm; index++)
                    if (index - position > 255) throw new RpgCompileException(program.MainStatements[index].LineNumber, "Native CALL exceeds 255 arguments.");
        }
    }
    private static void ValidateNativeFileIndicators(RpgOpcode op, string[] slots, bool error, int line)
    {
        var high = op is RpgOpcode.Chain or RpgOpcode.SetLl or RpgOpcode.SetGt;
        var equal = op is RpgOpcode.Read or RpgOpcode.ReadP or RpgOpcode.ReadC or RpgOpcode.Reade or RpgOpcode.ReadPe or RpgOpcode.SetLl;
        if (!high && slots[0] != "  " || !equal && slots[2] != "  " || error && slots[1] != "  ")
            throw new RpgCompileException(line, "Unsupported native file-result indicator slot or E plus error indicator.");
    }

    private static void NativeNumericOperand(RpgProgram program, string text, int line, bool variable = false, bool integer = false, bool array = true, bool requireArray = false)
    {
        var expression = RpgExpressionParser.Parse(text);
        var layout = FixedLookupLayout(program, expression);
        var literal = expression;
        while (literal is RpgUnary unary) literal = unary.Operand;
        var literalValue = literal is RpgLiteral number ? number.Value : null;
        var numericLiteral = RpgValues.IsNumeric(literalValue);
        var valid = layout?.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Float or RpgFieldKind.Decimal;
        var invalidCounter = integer && (layout?.Kind == RpgFieldKind.Float || layout is { Decimals: not 0 } || numericLiteral && RpgValues.IsFloat(literalValue));
        if (variable && (layout?.Constant == true || layout?.ReadOnly == true) || requireArray && layout?.IsArray != true ||
            layout?.IsArray == true && !array || invalidCounter || !valid && (variable || requireArray || !numericLiteral))
            throw new RpgCompileException(line, "Native arithmetic requires declared numeric fields/arrays or numeric literals; flags/counters require the supported scalar layout.");
        if (integer && numericLiteral && DeclarationValue(program, text, line) is decimal value && (decimal.GetBits(value)[3] & 0x00FF0000) != 0)
            throw new RpgCompileException(line, "Native counters and steps require zero decimal positions.");
    }

    private static RpgOpcode MapNativeOpcode(string opcode) => opcode switch
    {
        "ADD" => RpgOpcode.Add, "SUB" => RpgOpcode.Subtract, "MULT" => RpgOpcode.Multiply, "DIV" => RpgOpcode.Divide,
        "Z-ADD" => RpgOpcode.ZAdd, "Z-SUB" => RpgOpcode.ZSub, "XFOOT" => RpgOpcode.XFoot, "COMP" => RpgOpcode.Comp, "LOOKUP" => RpgOpcode.Lookup,
        "SETON" => RpgOpcode.SetOn, "SETOFF" => RpgOpcode.SetOff, "CLEAR" => RpgOpcode.Clear,
        "DO" => RpgOpcode.Do, "ENDDO" => RpgOpcode.EndDo, "ENDFOR" => RpgOpcode.EndDo, "ELSE" => RpgOpcode.Else,
        "ENDIF" => RpgOpcode.EndIf, "SELECT" => RpgOpcode.Select, "OTHER" => RpgOpcode.Other, "ENDSL" => RpgOpcode.EndSl,
        "ITER" => RpgOpcode.Iter, "LEAVE" => RpgOpcode.Leave, "BEGSR" => RpgOpcode.BegSr, "ENDSR" => RpgOpcode.EndSr,
        "EXSR" => RpgOpcode.Exsr, "GOTO" => RpgOpcode.Goto, "TAG" => RpgOpcode.Tag, "MONITOR" => RpgOpcode.Monitor,
        "ENDMON" => RpgOpcode.EndMon, "PLIST" => RpgOpcode.Plist, "PARM" => RpgOpcode.Parm, "CALL" => RpgOpcode.Call,
        "OPEN" => RpgOpcode.Open, "CLOSE" => RpgOpcode.Close, "UNLOCK" => RpgOpcode.Unlock, "CHAIN" => RpgOpcode.Chain,
        "READ" => RpgOpcode.Read, "READP" => RpgOpcode.ReadP, "READC" => RpgOpcode.ReadC, "READE" => RpgOpcode.Reade,
        "READPE" => RpgOpcode.ReadPe, "SETLL" => RpgOpcode.SetLl, "SETGT" => RpgOpcode.SetGt, "WRITE" => RpgOpcode.Write,
        "UPDATE" => RpgOpcode.Update, "DELETE" => RpgOpcode.Delete, "EXFMT" => RpgOpcode.ExFmt, "DSPLY" => RpgOpcode.Dsply,
        _ => RpgOpcode.None,
    };
    private static RpgOpcode MapNativeExtendedOpcode(string opcode) => opcode switch
    {
        "EVAL" => RpgOpcode.Eval, "IF" => RpgOpcode.If, "WHEN" => RpgOpcode.When, "DOW" => RpgOpcode.DoW,
        "DOU" => RpgOpcode.DoU, "FOR" => RpgOpcode.For, "CALLP" => RpgOpcode.CallP, "ON-ERROR" => RpgOpcode.OnError,
        "RETURN" => RpgOpcode.Return, "SORTA" => RpgOpcode.SortA, "SORTA(A)" => RpgOpcode.SortA, "SORTA(D)" => RpgOpcode.SortA,
        _ => RpgOpcode.None,
    };
}
