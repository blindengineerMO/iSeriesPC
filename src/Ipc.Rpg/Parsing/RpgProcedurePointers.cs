using Ipc.Core.Text;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    public static IReadOnlySet<string> ProcedureAddressSymbols(RpgProgram module) => module.ProcedureAddresses.Concat(module.Subprocedures.Where(p => p.NativeBody is not null).SelectMany(p => p.NativeBody!.ProcedureAddresses)).ToHashSet(StringComparer.Ordinal);
    private static bool ContainsPointers(RpgDataStructure structure) => PointerElements(structure).Any();
    public static string? ProcedureAddressSymbol(RpgProgram program, RpgExpr argument, int line = 0, bool initialization = false)
    {
        if (argument is RpgFieldRef name && program.FindPrototype(name.Name) is { } prototype)
        {
            if (!prototype.NativeProcedure) throw new RpgCompileException(line, "%PADDR requires a bound procedure prototype.");
            if (prototype.PointerTarget is not null)
            {
                if (initialization) throw new RpgCompileException(line, "Pointer-based %PADDR cannot initialize a definition.");
                return null;
            }
            return prototype.ExternalName ?? prototype.Name;
        }
        var value = argument switch
        {
            RpgLiteral literal => literal.Value,
            RpgHexLiteral hex => hex.ToArray(),
            RpgFieldRef field => DeclarationValue(program, field.Name, line),
            _ => throw new RpgCompileException(line, "%PADDR requires a character constant or bound prototype.")
        };
        var symbol = value is byte[] bytes ? CodePage.FromCcsid(37).GetString(bytes) : value as string;
        if (!RpgExportLevel.SymbolName(symbol)) throw new RpgCompileException(line, "Invalid procedure address symbol.");
        return symbol;
    }
    private static void CollectProcedureAddresses(RpgProgram program)
    {
        foreach (var unit in new[] { program }.Concat(program.Subprocedures.Where(p => p.NativeBody is not null).Select(p => p.NativeBody!)))
        {
            var texts = unit.MainStatements.Concat(unit.Subroutines.SelectMany(s => s.Statements)).SelectMany(s => new[] { s.Factor1, s.Factor2, s.Result, s.Value }.OfType<string>().Select(t => (Text: t, Line: s.LineNumber, Init: false)))
                .Concat(unit.DataStructures.Where(d => !unit.GlobalStructures.Contains(d)).SelectMany(d => PointerElements(d)).Where(e => e.InitialValue is not null).Select(e => (Text: e.InitialValue!, Line: e.DeclarationLine, Init: true)));
            foreach (var text in texts)
                for (var index = 0; index < text.Text.Length; index++)
                {
                    if (text.Text[index] == '\'') { Skip(text.Text, ref index); continue; }
                    if (index + 6 > text.Text.Length || !text.Text.AsSpan(index, 6).Equals("%PADDR".AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                    var opening = index + 6;
                    while (opening < text.Text.Length && char.IsWhiteSpace(text.Text[opening])) opening++;
                    if (opening == text.Text.Length || text.Text[opening] != '(') continue;
                    var start = opening + 1; var close = start; var depth = 1;
                    for (; close < text.Text.Length; close++)
                    {
                        if (text.Text[close] == '\'') { Skip(text.Text, ref close); continue; }
                        if (text.Text[close] == '(') depth++;
                        if (text.Text[close] == ')' && --depth == 0) break;
                    }
                    if (close == text.Text.Length) throw new RpgCompileException(text.Line, "Unclosed %PADDR.");
                    var symbol = ProcedureAddressSymbol(unit, RpgExpressionParser.Parse(text.Text[start..close]), text.Line, text.Init);
                    if (symbol is not null && !unit.ProcedureAddresses.Contains(symbol, StringComparer.Ordinal)) unit.ProcedureAddresses.Add(symbol);
                    if (symbol is not null && !unit.IsModule && (unit.FindSubprocedure(symbol) is not { NativeBody: not null } local || local.Name != symbol)) throw new RpgCompileException(text.Line, "Unresolved procedure address: " + symbol);
                    index = close;
                }
        }
        static void Skip(string text, ref int index)
        { while (++index < text.Length) if (text[index] == '\'') { if (index + 1 < text.Length && text[index + 1] == '\'') index++; else break; } }
    }
    private static IEnumerable<RpgDsElement> PointerElements(RpgDataStructure structure) => structure.Elements.SelectMany(e => e.NestedStructure is { } nested ? PointerElements(nested) : (e.Kind is RpgFieldKind.ProcedurePointer or RpgFieldKind.DataPointer) ? [e] : []);
}
