using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal void AssignRight(RpgExpr target, RpgExpr expression)
    {
        expression = RpgCompiler.RightArrayExpression(_program, expression);
        object? value;
        if (TryNativeReference(expression, out var source) && (source.Element is null || source.Element.Kind == RpgFieldKind.Character))
        {
            EnsureArgumentStorage(source);
            value = source.IsArray ? Enumerable.Range(1, source.Count).Select(index => (object?)NativeBuffer(NativeIndex(source, index))).ToArray() : NativeBuffer(source);
        }
        else value = expression.Eval(this);
        var view = RightAssignmentTarget(target); EnsureNativeWritable(view);
        if (!view.IsArray && value is object?[]) throw NativeFailure(105, "EVALR cannot assign an array expression to a scalar.");
        object Adjust(object? input)
        {
            _host.CancellationToken.ThrowIfCancellationRequested();
            var bytes = CharacterArgumentBytes(input); var width = view.UnitWidth;
            var result = Enumerable.Repeat(NativeBlank, width).ToArray(); var count = Math.Min(width, bytes.Length);
            bytes.AsSpan(bytes.Length - count, count).CopyTo(result.AsSpan(width - count));
            return new ProgramBuffer(result, NativeCcsid);
        }
        var prepared = value is object?[] values ? values.Take(view.Count).Select(Adjust).ToArray() : Adjust(value);
        WriteNative(view, prepared);
    }
    private long RightSubstringIndex(RpgExpr expression)
    {
        var value = expression.Eval(this); var type = RpgCompiler.ExactActualType(_program, expression);
        if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value) || type is { Decimals: > 0 } or { Kind: RpgFieldKind.Float })
            throw NativeFailure(105, "EVALR substring positions require integer numeric values.");
        var number = RpgValues.ToDecimal(value);
        if ((decimal.GetBits(number)[3] & 0x00FF0000) != 0 || number != decimal.Truncate(number))
            throw NativeFailure(105, "EVALR substring positions cannot have decimal positions.");
        if (number < long.MinValue || number > long.MaxValue) throw NativeFailure(100, "EVALR substring position exceeds its bounds.");
        return (long)number;
    }
    private NativeView RightAssignmentTarget(RpgExpr expression)
    {
        if (expression is RpgAllElementsRef all) expression = all.Target;
        if (expression is not RpgBuiltin { Name: "SUBST", Arguments.Count: 2 or 3 } part)
        {
            if (!TryNativeReference(expression, out var direct) || direct.Element is not null && direct.Element.Kind != RpgFieldKind.Character)
                throw NativeFailure(105, "EVALR requires native fixed-length character storage.");
            return direct;
        }
        if (!TryNativeReference(part.Arguments[0], out var owner) || owner.IsArray || owner.Element is not null && owner.Element.Kind != RpgFieldKind.Character)
            throw NativeFailure(105, "EVALR substring targets require scalar character storage.");
        var start = RightSubstringIndex(part.Arguments[1]);
        var count = part.Arguments.Count == 3 ? RightSubstringIndex(part.Arguments[2]) : owner.Width - start + 1;
        if (start < 1 || start > owner.Width + 1 || count < 0 || count > owner.Width - start + 1)
            throw NativeFailure(100, "EVALR substring exceeds its declared character size.");
        if (owner.Binding?.Memory is { } memory && start - 1 + count > memory.Capacity)
            throw NativeFailure(105, "EVALR substring exceeds the actual argument storage.");
        var field = new RpgDsElement { Name = "EVALR_SUBSTRING", Kind = RpgFieldKind.Character, Length = (int)count, StorageLength = (int)count,
            ReadOnlyParameter = owner.Element?.ReadOnlyParameter == true, OpaqueBytes = owner.Element?.OpaqueBytes == true };
        var view = new NativeView(owner.Structure, owner.Bytes, owner.Offset + (int)start - 1, (int)count, field, false, 1);
        return BindNativeSubview(owner, view, (int)start - 1, (int)count, new("Character", (int)count, 0, 1));
    }
}
