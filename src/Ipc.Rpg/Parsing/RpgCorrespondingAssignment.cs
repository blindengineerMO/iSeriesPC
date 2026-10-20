using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ParseCorrespondingAssignment(RpgProgram program, RpgStatement statement, string operands, int line)
    {
        var quoted = false; var depth = 0; var split = -1;
        for (var index = 0; index < operands.Length; index++)
        {
            if (operands[index] == '\'') { if (quoted && index + 1 < operands.Length && operands[index + 1] == '\'') index++; else quoted = !quoted; continue; }
            if (quoted) continue;
            if (operands[index] == '(') depth++;
            else if (operands[index] == ')') depth--;
            else if (depth == 0 && operands[index] == '=') { split = index; break; }
        }
        if (split < 1 || split == operands.Length - 1) throw new RpgCompileException(line, "EVAL-CORR requires target = source data structure variables.");
        statement.Result = operands[..split].Trim(); statement.Factor2 = operands[(split + 1)..].Trim();
        RpgField Type(string text)
        {
            var expression = RpgExpressionParser.Parse(text);
            var selected = expression is RpgBuiltin { Name: "SUBARR", Arguments.Count: 2 or 3 } slice ? slice.Arguments[0] : expression;
            if (!Variable(selected) || ExactActualType(program, selected) is not { IsDataStructure: true, ParameterStructure.NativeStorage: true } type ||
                expression is RpgBuiltin && !type.IsArray)
                throw new RpgCompileException(line, "EVAL-CORR operands require native data structure variables or their array slices.");
            return type;
        }
        try
        {
            var target = Type(statement.Result); var source = Type(statement.Factor2);
            if (source.IsArray && !target.IsArray) throw new RpgCompileException(line, "An EVAL-CORR array source requires an array target.");
            if (target.ReadOnlyParameter) throw new RpgCompileException(line, "EVAL-CORR cannot change a CONST target.");
        }
        catch (RpgCompileException error) when (error.LineNumber == 0) { throw new RpgCompileException(line, error.Message); }
        bool Variable(RpgExpr expression) => expression switch {
            RpgFieldRef => true,
            RpgIndexedRef indexed => program.FindPrototype(indexed.Name) is null,
            RpgDynamicIndexRef indexed => program.FindPrototype(indexed.Name) is null,
            RpgMemberRef member => Variable(member.Target), RpgElementRef element => Variable(element.Target), _ => false };
    }
    internal static IEnumerable<(RpgDsElement Source, RpgDsElement Target)> CorrespondingFields(RpgDataStructure source, RpgDataStructure target)
    {
        foreach (var field in source.Elements.Where(field => !field.Anonymous))
        {
            var found = target.Find(field.LocalName ?? field.Name);
            if (found is null || found.Anonymous || field.IsArray != found.IsArray) continue;
            if (field.NestedStructure is not null || found.NestedStructure is not null)
            { if (field.NestedStructure is not null && found.NestedStructure is not null) yield return (field, found); continue; }
            if (field.Kind == found.Kind || Numeric(field.Kind) && Numeric(found.Kind) ||
                field.Kind is RpgFieldKind.Character or RpgFieldKind.Indicator && found.Kind is RpgFieldKind.Character or RpgFieldKind.Indicator)
                yield return (field, found);
        }
        static bool Numeric(RpgFieldKind kind) => kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Integer or RpgFieldKind.Float or RpgFieldKind.Decimal or RpgFieldKind.Binary;
    }
}
