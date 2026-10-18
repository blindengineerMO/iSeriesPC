using System.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal int CompareNativeCalculation(RpgExpr lhs, RpgExpr rhs)
    {
        object? Value(RpgExpr expression)
        {
            if (TryNativeReference(expression, out var view))
            {
                if (view.IsArray) throw NativeFailure(105, "Native COMP requires scalar operands.");
                return ArrayKey(view);
            }
            var value = expression.Eval(this);
            if (value is string text)
            {
                try { return NativeEncoding.GetBytes(text); }
                catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
            }
            return value is ProgramBuffer raw ? raw.Ccsid == NativeCcsid ? raw.ToArray() : throw NativeFailure(452, "Native COMP CCSIDs differ.") : value;
        }
        var left = Value(lhs); var right = Value(rhs);
        if ((left is byte[]) != (right is byte[]) || left is object?[] || right is object?[])
            throw NativeFailure(105, "Native COMP requires compatible scalar operands.");
        return CompareArrayValues(left, right);
    }
}

public sealed partial class RpgInterpreter
{
    private IReadOnlyList<string> NativeCallFields(RpgStatement statement, RpgRuntimeContext context)
    {
        if (statement.Factor2 is { } list)
            return context.Program.NativeParameterLists.TryGetValue((_procedure ?? "") + "/" + list, out var fields) ? fields :
                throw new RpgRuntimeException("Native CALL parameter metadata is missing.") { StatusCode = 202 };
        var frame = _executionFrames.Peek(); var result = new List<string>();
        for (var index = frame.Pc + 1; index < frame.Statements.Count && frame.Statements[index].Opcode == RpgOpcode.Parm; index++)
        {
            if (result.Count >= 255) throw new RpgRuntimeException("Native CALL exceeds 255 arguments.") { StatusCode = 202 };
            result.Add(frame.Statements[index].Factor1!);
        }
        return result;
    }
    private int NativeArithmetic(RpgStatement statement, RpgRuntimeContext context)
    {
        var target = RpgExpressionParser.Parse(statement.Result!);
        var native = context.TryNativeReference(target, out var view);
        var source = RpgExpressionParser.Parse(statement.Factor2!); object? value;
        if (statement.Opcode == RpgOpcode.XFoot) value = new RpgBuiltin("XFOOT", [source]).Eval(context);
        else if (statement.Opcode is RpgOpcode.ZAdd or RpgOpcode.ZSub)
        {
            var operand = source.Eval(context);
            object Convert(object? item) => statement.Opcode == RpgOpcode.ZSub ? RpgValues.Negate(item) : RpgValues.Positive(item);
            value = operand is object?[] items ? items.Select(Convert).ToArray() : Convert(operand);
        }
        else
        {
            var left = statement.Factor1 is { } factor ? RpgExpressionParser.Parse(factor) :
                new RpgLiteral(native ? context.ReadNative(view) : target.Eval(context));
            var op = statement.Opcode switch { RpgOpcode.Add => "+", RpgOpcode.Subtract => "-", RpgOpcode.Multiply => "*", _ => "/" };
            value = new RpgBinary(left, op, source).Eval(context);
        }
        if (native) context.WriteCapturedNative(view, value); else WriteTarget(statement.Result!, value, context);
        if (statement.Indicator1 != 0 || statement.Indicator2 != 0 || statement.Indicator3 != 0)
            ApplyArithmeticIndicators(statement, native ? context.ReadNative(view) : target.Eval(context), context);
        return -1;
    }
}
