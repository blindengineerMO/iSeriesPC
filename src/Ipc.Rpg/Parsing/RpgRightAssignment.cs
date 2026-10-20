using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void ParseRightAssignment(RpgStatement statement, string operands, int line)
    {
        var quote = false; var depth = 0; var split = -1;
        for (var index = 0; index < operands.Length; index++)
        {
            if (operands[index] == '\'') { if (quote && index + 1 < operands.Length && operands[index + 1] == '\'') index++; else quote = !quote; continue; }
            if (quote) continue;
            if (operands[index] == '(') depth++; else if (operands[index] == ')') depth--;
            else if (depth == 0 && operands[index] == '=') { split = index; break; }
        }
        if (split < 1 || split == operands.Length - 1) throw new RpgCompileException(line, "EVALR requires a character target = expression assignment.");
        statement.Result = operands[..split].Trim(); statement.Factor2 = operands[(split + 1)..].Trim();
    }
    internal static RpgExpr RightArrayExpression(RpgProgram program, RpgExpr expression, int line = 0) => expression switch
    {
        RpgAllElementsRef all => ExactActualType(program, all.Target) is { IsArray: true } ? all.Target : throw new RpgCompileException(line, "EVALR (*) requires an array operand."),
        RpgBinary binary => new RpgBinary(RightArrayExpression(program, binary.Lhs, line), binary.Op, RightArrayExpression(program, binary.Rhs, line)),
        RpgUnary unary => new RpgUnary(unary.Op, RightArrayExpression(program, unary.Operand, line)), _ => expression
    };
    private static void ValidateRightAssignments(RpgProgram program)
    {
        foreach (var statement in program.MainStatements.Where(s => s.Opcode == RpgOpcode.EvalR))
        {
            try
            {
                var target = RpgExpressionParser.Parse(statement.Result!); var source = RightArrayExpression(program, RpgExpressionParser.Parse(statement.Factor2!), statement.LineNumber);
                RpgField? TargetType(RpgExpr expression) => expression switch {
                    RpgAllElementsRef all => TargetType(all.Target) is { IsArray: true } array ? array : null,
                    RpgBuiltin { Name: "SUBARR", Arguments.Count: 2 or 3 } slice => TargetType(slice.Arguments[0]) is { IsArray: true } array ? array : null,
                    RpgBuiltin { Name: "SUBST", Arguments.Count: 2 or 3 } part => Variable(part.Arguments[0]) && ExactActualType(program, part.Arguments[0]) is { IsArray: false } scalar ? scalar : null,
                    _ => Variable(expression) ? ExactActualType(program, expression) : null };
                bool Variable(RpgExpr expression) => expression switch {
                    RpgFieldRef => true, RpgMemberRef member => Variable(member.Target), RpgElementRef element => Variable(element.Target),
                    RpgIndexedRef indexed => program.FindPrototype(indexed.Name) is null, RpgDynamicIndexRef indexed => program.FindPrototype(indexed.Name) is null, _ => false };
                var type = TargetType(target);
                if (type is not { Kind: RpgFieldKind.Character } || type.ReadOnlyParameter)
                    throw new RpgCompileException(statement.LineNumber, "EVALR requires a writable fixed-length character or data structure target.");
                var selectedSource = source is RpgAllElementsRef all ? all.Target : source;
                var input = ExactActualType(program, selectedSource is RpgBuiltin { Name: "SUBARR", Arguments.Count: > 0 } slice ? slice.Arguments[0] : selectedSource);
                if (input is not null && input.Kind is not (RpgFieldKind.Character or RpgFieldKind.VaryingChar) || source is RpgLiteral { Value: not string } ||
                    input?.IsArray == true && !type.IsArray)
                    throw new RpgCompileException(statement.LineNumber, "EVALR requires a character expression and an array target for an array source.");
            }
            catch (RpgCompileException error) when (error.LineNumber == 0) { throw new RpgCompileException(statement.LineNumber, error.Message); }
        }
    }
}
